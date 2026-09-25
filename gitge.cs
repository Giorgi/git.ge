#!/usr/bin/env dotnet
#:property PublishAot=false
#:property Nullable=enable

// git.ge tooling. Run from the repository root:
//   dotnet run gitge.cs -- discover [--only <term>] [--max-users <n>] [--restart]
//   dotnet run gitge.cs -- refresh [--force]
//   dotnet run gitge.cs -- review [--top <n>] > review.md
//   dotnet run gitge.cs -- trend | prune | selftest | prepare
// See docs/data-model.md for the files these commands read and write.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var command = args.FirstOrDefault();
var paths = new Paths(Directory.GetCurrentDirectory());

if (!File.Exists(paths.DiscoveryConfig))
{
    Log.Error($"Run from the repository root (missing {paths.DiscoveryConfig}).");
    return 1;
}

try
{
    var options = Options.Parse(args.Skip(1).ToArray());
    switch (command)
    {
        case "discover":
            using (var github = new GitHub(GitHub.ResolveToken()))
                await Discovery.Run(github, paths, options);
            return 0;
        case "refresh":
            using (var github = new GitHub(GitHub.ResolveToken()))
                await Refresh.Run(github, paths, options);
            return 0;
        case "trend":
            TrendReport.Run(paths);
            return 0;
        case "prune":
            var (months, deleted) = Pruning.Run(new SnapshotStore(paths.Snapshots));
            Log.Info($"Pruned: {months} month(s) rolled up, {deleted} daily snapshot(s) deleted");
            return 0;
        case "prepare":
            Prepare.Run(paths);
            return 0;
        case "selftest":
            return SelfTest.Run(paths);
        case "review":
            Review.Run(paths, options);
            return 0;
        default:
            Console.Error.WriteLine("usage: dotnet run gitge.cs -- <discover|refresh|review> [options]");
            Console.Error.WriteLine("  discover [--only <location term>] [--max-users <n>] [--restart]");
            Console.Error.WriteLine("  refresh [--force]    --force re-fetches repos already refreshed today");
            Console.Error.WriteLine("  review [--top <n>]   Markdown list of the most visible developers, for curation");
            Console.Error.WriteLine("  trend                Show the current trend window and top gainers");
            Console.Error.WriteLine("  prune                Roll daily snapshots older than 90 days into monthly ones");
            Console.Error.WriteLine("  selftest             Test trending and pruning against tests/fixtures");
            Console.Error.WriteLine("  prepare              Write _build/site-data.json for the site renderer");
            return 2;
    }
}
catch (Exception ex)
{
    Log.Error(ex.ToString());
    return 1;
}

// ---------------------------------------------------------------------------
// Discovery: find developers by location, admit their repos that pass the bar.
// ---------------------------------------------------------------------------

static class Discovery
{
    const int OwnersPerQuery = 20;

    public static async Task Run(GitHub github, Paths paths, Options options)
    {
        var config = Store.Read<DiscoveryConfig>(paths.DiscoveryConfig);
        var manualDevelopers = Store.Read<ManualDevelopers>(paths.ManualDevelopers);
        var optOut = Store.Read<OptOut>(paths.OptOut);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var isFullRun = options.Only is null && options.MaxUsers is null;

        var blocked = Logins(manualDevelopers.Exclude.Concat(optOut.Developers));
        var blockedProjects = new HashSet<string>(optOut.Projects, StringComparer.OrdinalIgnoreCase);

        // A crashed or timed-out run leaves a checkpoint; pick up where it stopped.
        var state = LoadCheckpoint(paths, options);

        // 1. Collect candidate owners: location search + include list.
        if (state.Candidates.Count == 0)
        {
            await SearchCandidates(github, config, options, blocked, state.Candidates);
            foreach (var login in manualDevelopers.Include)
                if (!blocked.Contains(login))
                    state.Candidates[login] = null;
            Store.WriteObject(paths.DiscoveryState, state);
        }
        var candidates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (login, term) in state.Candidates)
            candidates.TryAdd(login, term);
        var fetched = Logins(state.Fetched);
        var seenOwners = Logins(state.Seen);
        var pending = candidates.Keys.Where(l => !fetched.Contains(l)).ToList();

        Log.Info($"{candidates.Count} candidate owners, {pending.Count} still to fetch");

        // 2. Fetch each owner's public, non-fork repos in batches and apply the quality bar.
        var developers = Store.ReadList<Developer>(paths.Developers).ToDictionary(d => d.Login, StringComparer.OrdinalIgnoreCase);
        var projects = Store.ReadList<Project>(paths.Projects).ToDictionary(p => p.Key);
        var includeSet = Logins(manualDevelopers.Include);
        int admittedTotal = 0, newProjects = 0;

        var progress = new Progress("owners", pending.Count);
        var sinceCheckpoint = Stopwatch.StartNew();
        foreach (var batch in pending.Chunk(OwnersPerQuery))
        {
            var owners = await FetchOwners(github, batch);
            progress.Advance(batch.Length);
            foreach (var owner in owners)
            {
                var admitted = owner.Repos
                    .Where(r => !blockedProjects.Contains(r.FullName))
                    .Where(r => config.QualityBar.Passes(r.FullName, r.Description, r.IsFork, r.IsArchived, r.TemplateFrom, r.Stars, r.PushedAt, DateTimeOffset.UtcNow))
                    .ToList();
                if (admitted.Count == 0) continue;

                seenOwners.Add(owner.Login);
                developers.TryGetValue(owner.Login, out var existing);
                developers[owner.Login] = new Developer
                {
                    Login = owner.Login,
                    Id = owner.Id,
                    Type = owner.Type,
                    Name = owner.Name,
                    Location = owner.Location,
                    HtmlUrl = owner.HtmlUrl,
                    MatchedTerm = candidates.GetValueOrDefault(owner.Login),
                    Source = includeSet.Contains(owner.Login) ? "include" : "search",
                    FirstSeenAt = existing?.FirstSeenAt ?? today.ToString("yyyy-MM-dd"),
                };

                foreach (var repo in admitted)
                {
                    var key = Project.KeyFor(repo.Id);
                    if (!projects.TryGetValue(key, out var project))
                    {
                        project = new Project { Key = key, FirstSeenAt = today.ToString("yyyy-MM-dd") };
                        projects[key] = project;
                        newProjects++;
                    }
                    project.ApplyDiscovery(repo, owner.Type);
                    project.Source = "discovered";
                    admittedTotal++;
                }
            }

            fetched.UnionWith(batch);
            if (sinceCheckpoint.Elapsed >= CheckpointInterval)
            {
                // Data first, then state: if we die in between, those owners are simply fetched again.
                WriteData(paths, developers, projects);
                state.Fetched = [.. fetched];
                state.Seen = [.. seenOwners];
                Store.WriteObject(paths.DiscoveryState, state);
                sinceCheckpoint.Restart();
            }
        }

        // 3. Drop anything blocked; on a full run also drop owners no longer found.
        int removedDevelopers = 0, removedProjects = 0;
        foreach (var login in developers.Keys.ToList())
        {
            if (blocked.Contains(login) || (isFullRun && !seenOwners.Contains(login)))
            {
                developers.Remove(login);
                removedDevelopers++;
            }
        }
        foreach (var project in projects.Values.ToList())
        {
            var ownerGone = project.Source == "discovered" && !developers.ContainsKey(project.Owner);
            if (blocked.Contains(project.Owner) || blockedProjects.Contains(project.FullName) || ownerGone)
            {
                projects.Remove(project.Key);
                removedProjects++;
            }
        }

        WriteData(paths, developers, projects);
        File.Delete(paths.DiscoveryState);
        Log.Info($"Discovery done: {developers.Count} developers, {projects.Count} projects " +
                 $"({newProjects} new, {admittedTotal} passed the bar, removed {removedDevelopers} developers / {removedProjects} projects)" +
                 (isFullRun ? "" : " [partial run: owners not seen were kept]"));
        github.LogUsage();
    }

    static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan CheckpointMaxAge = TimeSpan.FromDays(14);

    static DiscoveryState LoadCheckpoint(Paths paths, Options options)
    {
        var fresh = new DiscoveryState { StartedAt = DateTimeOffset.UtcNow, Only = options.Only, MaxUsers = options.MaxUsers };
        if (!File.Exists(paths.DiscoveryState)) return fresh;

        var saved = Store.Read<DiscoveryState>(paths.DiscoveryState);
        if (options.Restart)
        {
            Log.Info("--restart: discarding the saved checkpoint");
            return fresh;
        }
        if (DateTimeOffset.UtcNow - saved.StartedAt > CheckpointMaxAge)
        {
            Log.Warn($"Checkpoint from {saved.StartedAt:yyyy-MM-dd} is older than {CheckpointMaxAge.TotalDays} days; starting over");
            return fresh;
        }
        if (saved.Only != options.Only || saved.MaxUsers != options.MaxUsers)
            throw new InvalidOperationException(
                $"An unfinished discovery run (--only {saved.Only ?? "-"} --max-users {saved.MaxUsers?.ToString() ?? "-"}) has a checkpoint. " +
                "Rerun with the same options to resume it, or pass --restart to discard it.");

        Log.Info($"Resuming discovery started {saved.StartedAt:yyyy-MM-dd HH:mm} UTC: {saved.Fetched.Count} of {saved.Candidates.Count} owners already fetched");
        return saved;
    }

    static async Task SearchCandidates(GitHub github, DiscoveryConfig config, Options options,
                                       HashSet<string> blocked, Dictionary<string, string?> candidates)
    {
        var terms = options.Only is null ? config.Locations : config.Locations.Where(t => t.Equals(options.Only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (terms.Count == 0)
            throw new InvalidOperationException($"--only '{options.Only}' is not in config/discovery.json locations.");

        foreach (var term in terms)
        {
            int found = 0, rejected = 0;
            await foreach (var user in SearchUsers(github, term, config))
            {
                var login = user.Login;
                if (blocked.Contains(login)) continue;
                // Search matches location loosely; require the term to actually appear.
                if (user.Location is null || !user.Location.Contains(term, StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }
                if (!candidates.ContainsKey(login)) { candidates[login] = term; found++; }
                if (options.MaxUsers is { } max && candidates.Count >= max) break;
            }
            Log.Info($"'{term}': {found} new candidates, {rejected} rejected (location did not contain term)");
            if (options.MaxUsers is { } m && candidates.Count >= m) break;
        }
    }

    static void WriteData(Paths paths, Dictionary<string, Developer> developers, Dictionary<string, Project> projects)
    {
        Store.WriteList(paths.Developers, developers.Values.OrderBy(d => d.Login, StringComparer.OrdinalIgnoreCase));
        Store.WriteList(paths.Projects, projects.Values.OrderBy(p => p.Id));
    }

    // GitHub search returns at most 1,000 results per query. When a query reports
    // more than that, split its account-creation date range in half and recurse.
    static async IAsyncEnumerable<SearchUser> SearchUsers(GitHub github, string term, DiscoveryConfig config)
    {
        var from = DateOnly.Parse(config.SearchCreatedFrom);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        await foreach (var user in SearchRange(github, term, config.MinPublicRepos, from, to))
            yield return user;
    }

    const string SearchQuery = """
        query($q: String!, $after: String) {
          search(type: USER, query: $q, first: 100, after: $after) {
            userCount
            pageInfo { hasNextPage endCursor }
            nodes {
              ... on User { login location }
              ... on Organization { login location }
            }
          }
        }
        """;

    static async IAsyncEnumerable<SearchUser> SearchRange(GitHub github, string term, int minRepos, DateOnly from, DateOnly to)
    {
        var q = $"location:\"{term}\" repos:>={minRepos} created:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
        string? after = null;
        var first = true;
        while (true)
        {
            var data = await github.Search(SearchQuery, new() { ["q"] = q, ["after"] = after });
            var search = data["search"]!;
            if (first)
            {
                var count = search["userCount"]!.GetValue<int>();
                if (count > 1000 && from < to)
                {
                    var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
                    Log.Info($"  {q}: {count} results, splitting at {mid:yyyy-MM-dd}");
                    await foreach (var u in SearchRange(github, term, minRepos, from, mid)) yield return u;
                    await foreach (var u in SearchRange(github, term, minRepos, mid.AddDays(1), to)) yield return u;
                    yield break;
                }
                if (count > 1000)
                    Log.Warn($"  {q}: {count} results in a single day; only the first 1,000 are reachable");
                first = false;
            }

            foreach (var node in search["nodes"]!.AsArray())
            {
                var login = node?["login"]?.GetValue<string>();
                if (login is not null)
                    yield return new SearchUser(login, node!["location"]?.GetValue<string>());
            }

            var pageInfo = search["pageInfo"]!;
            if (!pageInfo["hasNextPage"]!.GetValue<bool>()) yield break;
            after = pageInfo["endCursor"]!.GetValue<string>();
        }
    }

    const string OwnerFields = """
        __typename login url
        ... on User { databaseId name location }
        ... on Organization { databaseId name location }
        """;

    const string RepoConnection = """
        repositories(first: 100, after: $AFTER, privacy: PUBLIC, ownerAffiliations: [OWNER], isFork: false,
                     orderBy: { field: PUSHED_AT, direction: DESC }) {
          pageInfo { hasNextPage endCursor }
          nodes { id databaseId nameWithOwner url description stargazerCount isFork isArchived pushedAt
                  templateRepository { nameWithOwner } }
        }
        """;

    // Owners with many large repos can make a batch time out; halve it until it fits.
    static async Task<List<Owner>> FetchOwners(GitHub github, string[] logins)
    {
        try
        {
            return await FetchOwnersOnce(github, logins);
        }
        catch (GatewayTimeoutException) when (logins.Length > 1)
        {
            Log.Warn($"  batch of {logins.Length} owners timed out; splitting");
            var half = logins.Length / 2;
            var owners = await FetchOwners(github, logins[..half]);
            owners.AddRange(await FetchOwners(github, logins[half..]));
            return owners;
        }
    }

    static async Task<List<Owner>> FetchOwnersOnce(GitHub github, string[] logins)
    {
        var query = new StringBuilder("query(");
        query.Append(string.Join(", ", logins.Select((_, i) => $"$l{i}: String!")));
        query.Append(") {\n");
        var variables = new Dictionary<string, object?>();
        for (var i = 0; i < logins.Length; i++)
        {
            query.Append($"o{i}: repositoryOwner(login: $l{i}) {{ {OwnerFields} {RepoConnection.Replace("$AFTER", "null")} }}\n");
            variables[$"l{i}"] = logins[i];
        }
        query.Append('}');

        var data = await github.GraphQL(query.ToString(), variables);
        var owners = new List<Owner>();
        for (var i = 0; i < logins.Length; i++)
        {
            var node = data[$"o{i}"];
            if (node is null) { Log.Warn($"  owner '{logins[i]}' not found (renamed or deleted?)"); continue; }
            var owner = Owner.From(node);
            var connection = node["repositories"]!;
            owner.Repos.AddRange(connection["nodes"]!.AsArray().Select(r => RepoSummary.From(r!)));
            // Rare: owners with more than 100 non-fork repos need further pages.
            while (connection["pageInfo"]!["hasNextPage"]!.GetValue<bool>())
            {
                var more = await github.GraphQL(
                    $"query($l: String!, $after: String) {{ o: repositoryOwner(login: $l) {{ {RepoConnection.Replace("$AFTER", "$after")} }} }}",
                    new() { ["l"] = owner.Login, ["after"] = connection["pageInfo"]!["endCursor"]!.GetValue<string>() });
                connection = more["o"]!["repositories"]!;
                owner.Repos.AddRange(connection["nodes"]!.AsArray().Select(r => RepoSummary.From(r!)));
            }
            owners.Add(owner);
        }
        return owners;
    }

    static HashSet<string> Logins(IEnumerable<string> logins) => new(logins, StringComparer.OrdinalIgnoreCase);

    record SearchUser(string Login, string? Location);
}

// ---------------------------------------------------------------------------
// Refresh: update every known repo's stats and write today's star snapshot.
// ---------------------------------------------------------------------------

static class Refresh
{
    const int ReposPerQuery = 100;

    public static async Task Run(GitHub github, Paths paths, Options options)
    {
        var config = Store.Read<DiscoveryConfig>(paths.DiscoveryConfig);
        var manualDevelopers = Store.Read<ManualDevelopers>(paths.ManualDevelopers);
        var manualProjects = Store.Read<List<ManualProject>>(paths.ManualProjects);
        var optOut = Store.Read<OptOut>(paths.OptOut);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var blocked = new HashSet<string>(manualDevelopers.Exclude.Concat(optOut.Developers), StringComparer.OrdinalIgnoreCase);
        var blockedProjects = new HashSet<string>(optOut.Projects, StringComparer.OrdinalIgnoreCase);
        var projects = Store.ReadList<Project>(paths.Projects).ToDictionary(p => p.Key);

        // Submitted GitHub projects that the bot doesn't know yet: resolve by name.
        var known = new HashSet<string>(projects.Values.Select(p => p.FullName), StringComparer.OrdinalIgnoreCase);
        var toResolve = manualProjects
            .Where(m => m.FullName is not null && !known.Contains(m.FullName) && !blockedProjects.Contains(m.FullName))
            .Select(m => m.FullName!)
            .ToList();
        foreach (var batch in toResolve.Chunk(50))
        {
            foreach (var (fullName, repo) in await ResolveByName(github, batch))
            {
                if (repo is null) { Log.Warn($"  submitted project '{fullName}' not found on GitHub"); continue; }
                var key = Project.KeyFor(repo["databaseId"]!.GetValue<long>());
                projects[key] = new Project { Key = key, NodeId = repo["id"]!.GetValue<string>(), Source = "submitted", FirstSeenAt = today };
            }
        }

        foreach (var project in projects.Values.ToList())
            if (blocked.Contains(project.Owner) || blockedProjects.Contains(project.FullName))
                projects.Remove(project.Key);

        // Refresh by node id, up to 100 repos per query. Repos already refreshed today
        // are skipped, so rerunning after a crash resumes instead of starting over.
        int updated = 0, removed = 0;
        var pending = projects.Values.Where(p => options.Force || p.RefreshedAt != today).ToList();
        if (pending.Count < projects.Count)
            Log.Info($"{projects.Count - pending.Count} repos already refreshed today; resuming with {pending.Count}");
        var progress = new Progress("repos", pending.Count);
        var sinceCheckpoint = Stopwatch.StartNew();
        foreach (var batch in pending.Chunk(ReposPerQuery))
        {
            if (sinceCheckpoint.Elapsed >= TimeSpan.FromMinutes(1))
            {
                Store.WriteList(paths.Projects, projects.Values.OrderBy(p => p.Id));
                sinceCheckpoint.Restart();
            }
            foreach (var (project, node) in await FetchRepos(github, batch, config.HelpWantedLabels))
            {
                if (node is null)
                {
                    Log.Warn($"  {project.FullName} no longer exists; removing");
                    projects.Remove(project.Key);
                    removed++;
                    continue;
                }
                project.ApplyRefresh(node, today);
                updated++;
            }
            progress.Advance(batch.Length);
        }

        // Renames can move a project onto an opted-out name/owner.
        foreach (var project in projects.Values.ToList())
            if (blocked.Contains(project.Owner) || blockedProjects.Contains(project.FullName))
                projects.Remove(project.Key);

        Store.WriteList(paths.Projects, projects.Values.OrderBy(p => p.Id));
        Store.WriteSnapshot(Path.Combine(paths.DailySnapshots, $"{today}.json"), today, projects.Values);

        // The /help-wanted page lists the issues themselves; fetch them only where the count says there are some.
        var issues = await FetchHelpWantedIssues(github, projects.Values.Where(p => p.HelpWantedIssues > 0).ToList(), config.HelpWantedLabels);
        Store.WriteList(paths.Issues, issues.OrderBy(i => i.Project).ThenBy(i => i.Number));

        Log.Info($"Refresh done: {updated} updated, {removed} removed, {toResolve.Count} submitted looked up, " +
                 $"{issues.Count} help-wanted issues; snapshot {today} written");
        github.LogUsage();
    }

    const int IssuesPerRepo = 10;

    static async Task<List<HelpWantedIssue>> FetchHelpWantedIssues(GitHub github, List<Project> projects, List<string> labels)
    {
        var issues = new List<HelpWantedIssue>();
        foreach (var batch in projects.Chunk(50))
        {
            var data = await github.GraphQL($$"""
                query($ids: [ID!]!, $labels: [String!]) {
                  nodes(ids: $ids) {
                    ... on Repository {
                      issues(first: {{IssuesPerRepo}}, states: OPEN, labels: $labels, orderBy: { field: UPDATED_AT, direction: DESC }) {
                        nodes { number title url createdAt labels(first: 10) { nodes { name } } }
                      }
                    }
                  }
                }
                """, new() { ["ids"] = batch.Select(p => p.NodeId).ToList(), ["labels"] = labels });
            var nodes = data["nodes"]!.AsArray();
            for (var i = 0; i < batch.Length; i++)
            {
                foreach (var issue in nodes[i]?["issues"]?["nodes"]?.AsArray() ?? [])
                {
                    issues.Add(new HelpWantedIssue
                    {
                        Project = batch[i].Key,
                        Number = issue!["number"]!.GetValue<int>(),
                        Title = issue["title"]!.GetValue<string>(),
                        Url = issue["url"]!.GetValue<string>(),
                        CreatedAt = Json.Date(issue["createdAt"]),
                        Labels = issue["labels"]!["nodes"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()).ToList(),
                    });
                }
            }
        }
        return issues;
    }

    // Repos with huge issue counts can make a batch time out; halve it until it fits.
    static async Task<List<(Project Project, JsonNode? Node)>> FetchRepos(GitHub github, Project[] batch, List<string> labels)
    {
        try
        {
            var data = await github.GraphQL(RefreshQuery, new() { ["ids"] = batch.Select(p => p.NodeId).ToList(), ["labels"] = labels });
            var nodes = data["nodes"]!.AsArray();
            return batch.Select((p, i) => (p, nodes[i])).ToList();
        }
        catch (GatewayTimeoutException) when (batch.Length > 1)
        {
            Log.Warn($"  batch of {batch.Length} repos timed out; splitting");
            var half = batch.Length / 2;
            var result = await FetchRepos(github, batch[..half], labels);
            result.AddRange(await FetchRepos(github, batch[half..], labels));
            return result;
        }
    }

    const string RefreshQuery = """
        query($ids: [ID!]!, $labels: [String!]) {
          nodes(ids: $ids) {
            ... on Repository {
              id databaseId nameWithOwner url description
              owner { __typename login }
              stargazerCount forkCount isFork isArchived pushedAt createdAt
              templateRepository { nameWithOwner }
              primaryLanguage { name }
              repositoryTopics(first: 20) { nodes { topic { name } } }
              openIssues: issues(states: OPEN) { totalCount }
              helpWanted: issues(states: OPEN, labels: $labels) { totalCount }
            }
          }
        }
        """;

    static async Task<List<(string FullName, JsonNode? Repo)>> ResolveByName(GitHub github, string[] fullNames)
    {
        var parameters = new List<string>();
        var fields = new StringBuilder();
        var variables = new Dictionary<string, object?>();
        for (var i = 0; i < fullNames.Length; i++)
        {
            var split = fullNames[i].Split('/', 2);
            parameters.Add($"$o{i}: String!, $n{i}: String!");
            variables[$"o{i}"] = split[0];
            variables[$"n{i}"] = split.Length > 1 ? split[1] : "";
            fields.Append($"r{i}: repository(owner: $o{i}, name: $n{i}) {{ id databaseId }}\n");
        }
        var data = await github.GraphQL($"query({string.Join(", ", parameters)}) {{\n{fields}}}", variables);
        return fullNames.Select((name, i) => (name, data[$"r{i}"])).ToList();
    }
}

// ---------------------------------------------------------------------------
// Trending: stars gained since the snapshot closest to N days before the latest
// one. Missing history gives null ("—"), never a guess.
// ---------------------------------------------------------------------------

sealed class Snapshot
{
    public string Date { get; set; } = "";
    public Dictionary<long, int> Stars { get; set; } = [];
}

sealed class SnapshotStore(string root)
{
    public string Daily => Path.Combine(root, "daily");
    public string Monthly => Path.Combine(root, "monthly");

    // Every available snapshot by date. Daily files are named by date; a monthly file
    // carries the date of the daily snapshot it was rolled up from.
    public SortedDictionary<DateOnly, string> List()
    {
        var files = new SortedDictionary<DateOnly, string>();
        if (Directory.Exists(Monthly))
            foreach (var file in Directory.GetFiles(Monthly, "*.json"))
                files[Dates.Parse(Load(file).Date)] = file;
        if (Directory.Exists(Daily))
            foreach (var file in Directory.GetFiles(Daily, "*.json"))
                if (Dates.TryParse(Path.GetFileNameWithoutExtension(file), out var date))
                    files[date] = file;
        return files;
    }

    public static Snapshot Load(string path) => Store.Read<Snapshot>(path);
}

sealed record TrendResult(DateOnly Current, DateOnly? Baseline, Dictionary<long, int?> Delta)
{
    // False until a snapshot old enough exists; the site then sorts by stars instead.
    public bool HasHistory => Baseline is not null;
}

static class Trending
{
    public static TrendResult Compute(SnapshotStore store, int windowDays, int toleranceDays)
    {
        var files = store.List();
        if (files.Count == 0)
            throw new InvalidOperationException("No snapshots found; run refresh first.");

        // "Now" is the latest snapshot, not the clock, so a missed nightly run doesn't skew the window.
        var current = files.Keys.Last();
        var baseline = PickBaseline(current, files.Keys, windowDays, toleranceDays);
        var currentStars = SnapshotStore.Load(files[current]).Stars;
        var baselineStars = baseline is { } b ? SnapshotStore.Load(files[b]).Stars : null;
        return new(current, baseline, Diff(currentStars, baselineStars));
    }

    // The snapshot closest to windowDays before current, within ± toleranceDays.
    // On a tie the older one wins, because it covers the whole window.
    public static DateOnly? PickBaseline(DateOnly current, IEnumerable<DateOnly> available, int windowDays, int toleranceDays)
    {
        var target = current.AddDays(-windowDays);
        return available
            .Where(d => d < current && Math.Abs(d.DayNumber - target.DayNumber) <= toleranceDays)
            .OrderBy(d => Math.Abs(d.DayNumber - target.DayNumber))
            .ThenBy(d => d)
            .Select(d => (DateOnly?)d)
            .FirstOrDefault();
    }

    // A repo absent from the baseline (new, or listed later) gets null rather than
    // its whole star count counted as "gained".
    public static Dictionary<long, int?> Diff(Dictionary<long, int> current, Dictionary<long, int>? baseline) =>
        current.ToDictionary(
            kv => kv.Key,
            kv => baseline is not null && baseline.TryGetValue(kv.Key, out var then) ? kv.Value - then : (int?)null);
}

// ---------------------------------------------------------------------------
// Pruning: every month that ended more than 90 days before the latest daily
// snapshot is reduced to monthly/YYYY-MM.json, that month's last daily snapshot.
// Whole months only, so a daily file can live up to ~120 days.
// ---------------------------------------------------------------------------

static class Pruning
{
    public const int KeepDailyDays = 90;

    public static (int Months, int Deleted) Run(SnapshotStore store, int keepDays = KeepDailyDays)
    {
        if (!Directory.Exists(store.Daily)) return (0, 0);
        var daily = Directory.GetFiles(store.Daily, "*.json")
            .Select(f => (Ok: Dates.TryParse(Path.GetFileNameWithoutExtension(f), out var d), Date: d, Path: f))
            .Where(f => f.Ok)
            .ToList();
        if (daily.Count == 0) return (0, 0);

        var cutoff = daily.Max(f => f.Date).AddDays(-keepDays);
        int months = 0, deleted = 0;
        foreach (var month in daily.GroupBy(f => new DateOnly(f.Date.Year, f.Date.Month, 1)))
        {
            var lastDayOfMonth = month.Key.AddMonths(1).AddDays(-1);
            if (lastDayOfMonth >= cutoff) continue;

            var last = month.MaxBy(f => f.Date);
            var target = Path.Combine(store.Monthly, $"{month.Key:yyyy-MM}.json");
            // An existing roll-up is kept only if it comes from later in the month.
            if (!File.Exists(target) || Dates.Parse(SnapshotStore.Load(target).Date) < last.Date)
            {
                Directory.CreateDirectory(store.Monthly);
                File.Copy(last.Path, target, overwrite: true);
            }
            foreach (var file in month)
            {
                File.Delete(file.Path);
                deleted++;
            }
            months++;
        }
        return (months, deleted);
    }
}

static class Dates
{
    public static DateOnly Parse(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParse(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

static class TrendReport
{
    public static void Run(Paths paths)
    {
        var site = Store.Read<SiteConfig>(paths.SiteConfig);
        var result = Trending.Compute(new SnapshotStore(paths.Snapshots), site.TrendWindowDays, site.TrendToleranceDays);
        var known = result.Delta.Values.Count(v => v is not null);
        Log.Info(result.HasHistory
            ? $"Latest snapshot {result.Current:yyyy-MM-dd}, baseline {result.Baseline:yyyy-MM-dd} ({result.Current.DayNumber - result.Baseline!.Value.DayNumber} days): {known} of {result.Delta.Count} repos have a trend"
            : $"Latest snapshot {result.Current:yyyy-MM-dd}: no snapshot {site.TrendWindowDays}±{site.TrendToleranceDays} days older yet, so no trends (the site sorts by stars)");
        if (!result.HasHistory) return;

        var names = Store.ReadList<Project>(paths.Projects).ToDictionary(p => p.Id, p => p.FullName);
        foreach (var (id, delta) in result.Delta.Where(kv => kv.Value is > 0).OrderByDescending(kv => kv.Value).Take(15))
            Console.WriteLine($"  +{delta,-5} {names.GetValueOrDefault(id, id.ToString())}");
    }
}

// ---------------------------------------------------------------------------
// Self-test: trending and pruning against the fixtures in tests/fixtures/.
// ---------------------------------------------------------------------------

static class SelfTest
{
    static int failures;

    public static int Run(Paths paths)
    {
        var d = Dates.Parse;

        Log.Info("Baseline selection (latest 2026-06-30, window 30 ± 7 days, target 2026-05-31)");
        var current = d("2026-06-30");
        Check(Trending.PickBaseline(current, [d("2026-06-01"), d("2026-05-25")], 30, 7) == d("2026-06-01"), "picks the snapshot closest to 30 days back");
        Check(Trending.PickBaseline(current, [d("2026-05-29"), d("2026-06-02")], 30, 7) == d("2026-05-29"), "on a tie, prefers the older snapshot");
        Check(Trending.PickBaseline(current, [d("2026-06-20"), d("2026-06-29"), current], 30, 7) is null, "cold start: nothing old enough gives no baseline");
        Check(Trending.PickBaseline(current, [d("2026-05-10")], 30, 7) is null, "a snapshot outside the tolerance is not used");
        Check(Trending.PickBaseline(current, [d("2026-05-24")], 30, 7) == d("2026-05-24"), "37 days back (edge of tolerance) is used");
        Check(Trending.PickBaseline(current, [d("2026-06-07")], 30, 7) == d("2026-06-07"), "23 days back (edge of tolerance) is used");
        Check(Trending.PickBaseline(current, [d("2026-05-15"), d("2026-06-15")], 30, 7) is null, "a gap around the target gives no baseline");

        var work = Path.Combine(Path.GetTempPath(), $"gitge-selftest-{Guid.NewGuid():N}");
        CopyDirectory(Path.Combine(paths.Root, "tests", "fixtures", "snapshots"), work);
        try
        {
            var store = new SnapshotStore(work);

            Log.Info("Trend over fixture snapshots");
            var result = Trending.Compute(store, 30, 7);
            Check(result.Current == d("2026-06-30"), "latest snapshot is 'now'");
            Check(result.Baseline == d("2026-06-01"), "baseline is 2026-06-01 (29 days), not 2026-05-25 (36 days)");
            Check(result.Delta.GetValueOrDefault(1) == 15, "repo 1: 10 → 25 stars is +15");
            Check(result.Delta.GetValueOrDefault(3) == -2, "repo 3: 50 → 48 stars is -2");
            Check(result.Delta.GetValueOrDefault(5) == 0, "repo 5: unchanged is 0");
            Check(result.Delta.ContainsKey(2) && result.Delta[2] is null, "repo 2: new since the baseline has no trend (null)");
            Check(!result.Delta.ContainsKey(4), "repo 4: gone from the latest snapshot is not reported");

            Log.Info("Pruning (cutoff 2026-04-01)");
            var (months, deleted) = Pruning.Run(store);
            Check(months == 2 && deleted == 4, $"February and March rolled up, 4 daily files deleted (got {months} months, {deleted} files)");
            Check(RolledUpFrom(store, "2026-02") == "2026-02-27", "monthly 2026-02 is February's last daily snapshot");
            Check(RolledUpFrom(store, "2026-03") == "2026-03-31", "monthly 2026-03 is March's last daily snapshot");
            Check(RolledUpFrom(store, "2026-01") == "2026-01-31", "existing monthly 2026-01 is left alone");
            Check(!File.Exists(Path.Combine(store.Daily, "2026-02-10.json")), "rolled-up daily files are deleted");
            Check(File.Exists(Path.Combine(store.Daily, "2026-04-02.json")), "April is kept: it ends after the cutoff");
            Check(Pruning.Run(store) == (0, 0), "a second prune changes nothing");
            Check(Trending.Compute(store, 30, 7).Baseline == d("2026-06-01"), "trend is unchanged after pruning");
            Check(Trending.Compute(store, 91, 7).Baseline == d("2026-03-31"), "a monthly snapshot can serve as a baseline (91-day window)");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        Log.Info(failures == 0 ? "All checks passed" : $"{failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static string? RolledUpFrom(SnapshotStore store, string month)
    {
        var path = Path.Combine(store.Monthly, $"{month}.json");
        return File.Exists(path) ? SnapshotStore.Load(path).Date : null;
    }

    static void Check(bool ok, string what)
    {
        if (!ok) failures++;
        Console.Error.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    }

    static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

// ---------------------------------------------------------------------------
// Prepare: merge bot data, curation and opt-outs, apply the listing rules and
// trend, and write _build/site-data.json for the site renderer (site/). Every
// field in that file is public: no locations, nothing hidden or opted out.
// ---------------------------------------------------------------------------

static class Prepare
{
    const int NewWithinDays = 30;
    const int ActiveWithinDays = 7;
    const int SectionSize = 12;
    const int SpotlightSize = 3;
    const int SpotlightMinStars = 20;

    public static void Run(Paths paths)
    {
        var discovery = Store.Read<DiscoveryConfig>(paths.DiscoveryConfig);
        var site = Store.Read<SiteConfig>(paths.SiteConfig);
        var categories = Store.Read<CategoryConfig>(paths.CategoryConfig);
        var manualDevelopers = Store.Read<ManualDevelopers>(paths.ManualDevelopers);
        var manualProjects = Store.Read<List<ManualProject>>(paths.ManualProjects);
        var optOut = Store.Read<OptOut>(paths.OptOut);
        var now = DateTimeOffset.UtcNow;
        var ignoreCase = StringComparer.OrdinalIgnoreCase;

        var blocked = new HashSet<string>(manualDevelopers.Exclude.Concat(optOut.Developers), ignoreCase);
        var hidden = new HashSet<string>(optOut.Projects, ignoreCase);
        var curation = manualProjects.Where(m => m.FullName is not null)
            .GroupBy(m => m.FullName!, ignoreCase).ToDictionary(g => g.Key, g => g.Last(), ignoreCase);
        var trend = Trending.Compute(new SnapshotStore(paths.Snapshots), site.TrendWindowDays, site.TrendToleranceDays);
        bool IsMaintainer(string owner) => owner.Equals(site.MaintainerLogin, StringComparison.OrdinalIgnoreCase);

        var projects = new List<SiteProject>();
        foreach (var p in Store.ReadList<Project>(paths.Projects))
        {
            if (blocked.Contains(p.Owner) || hidden.Contains(p.FullName)) continue;
            curation.TryGetValue(p.FullName, out var m);
            // Curated or submitted projects skip the bar; discovered ones are re-checked in case the bar changed.
            var curated = m is not null || p.Source == "submitted";
            if (!curated && !discovery.QualityBar.Passes(p.FullName, p.Description, p.IsFork, p.IsArchived, p.TemplateFrom, p.Stars, p.PushedAt, now))
                continue;

            projects.Add(new SiteProject
            {
                Key = p.Key,
                Name = p.FullName.Split('/')[1],
                FullName = p.FullName,
                Owner = p.Owner,
                Url = p.HtmlUrl,
                Description = m?.Description ?? p.Description,
                DescriptionKa = m?.DescriptionKa,
                Language = p.Language,
                Topics = p.Topics ?? [],
                Stars = p.Stars,
                Trend = trend.Delta.TryGetValue(p.Id, out var delta) ? delta : null,
                HelpWanted = p.HelpWantedIssues,
                PushedAt = p.PushedAt,
                CreatedAt = p.CreatedAt,
                Category = m?.Category ?? categories.Infer(p),
                Archived = p.IsArchived,
                Featured = m?.Featured ?? false,
                Verified = m?.Verified ?? false,
                Maintainer = IsMaintainer(p.Owner),
                Listed = curated || p.Stars >= site.ListingMinStars,
                Source = curated && p.Source == "submitted" ? "submitted" : "discovered",
            });
        }

        // Projects hosted outside GitHub: only what the submitter provided; no stats.
        foreach (var m in manualProjects.Where(m => m.FullName is null && m.Url is not null))
        {
            var owner = m.Owner ?? "";
            if (blocked.Contains(owner) || hidden.Contains(m.Url!)) continue;
            projects.Add(new SiteProject
            {
                Key = $"url:{m.Url}",
                Name = m.Name ?? m.Url!,
                Owner = owner,
                Url = m.Url!,
                Description = m.Description,
                DescriptionKa = m.DescriptionKa,
                Category = m.Category ?? "other",
                Featured = m.Featured ?? false,
                Verified = m.Verified ?? false,
                Maintainer = IsMaintainer(owner),
                Listed = true,
                Source = "submitted",
            });
        }

        var newThisMonth = projects
            .Where(p => p.CreatedAt >= now.AddDays(-NewWithinDays))
            .OrderByDescending(p => p.Stars ?? 0).ThenByDescending(p => p.CreatedAt)
            .Take(SectionSize).ToList();
        var shownAsNew = newThisMonth.Select(p => p.Key).ToHashSet();
        var recentlyActive = projects
            .Where(p => p.PushedAt >= now.AddDays(-ActiveWithinDays) && (p.Stars ?? 0) >= discovery.QualityBar.MinStars)
            .Where(p => !shownAsNew.Contains(p.Key))
            .OrderByDescending(p => p.PushedAt)
            .Take(SectionSize).ToList();

        // Search covers everything with a few stars; the rest would only be noise in the browser's index.
        var inSections = newThisMonth.Concat(recentlyActive).Select(p => p.Key).ToHashSet();
        var published = projects
            .Where(p => p.Listed || (p.Stars ?? 0) >= discovery.QualityBar.MinStars || inSections.Contains(p.Key))
            .OrderByDescending(p => p.Stars ?? 0).ThenBy(p => p.Key)
            .ToList();
        var publishedKeys = published.Select(p => p.Key).ToHashSet();

        var data = new SiteData
        {
            GeneratedAt = now,
            SiteUrl = site.SiteUrl,
            RepoUrl = site.RepoUrl,
            MaintainerLogin = site.MaintainerLogin,
            ListingMinStars = site.ListingMinStars,
            Trend = new SiteTrend
            {
                Current = trend.Current.ToString("yyyy-MM-dd"),
                Baseline = trend.Baseline?.ToString("yyyy-MM-dd"),
                WindowDays = site.TrendWindowDays,
            },
            Categories = categories.Order,
            Developers = published.Where(p => p.Listed).Select(p => p.Owner).Distinct(ignoreCase).Count(),
            Projects = published,
            NewThisMonth = newThisMonth.Select(p => p.Key).ToList(),
            RecentlyActive = recentlyActive.Select(p => p.Key).ToList(),
            Spotlight = PickSpotlight(published, now).Select(p => p.Key).ToList(),
            Issues = Store.ReadList<HelpWantedIssue>(paths.Issues).Where(i => publishedKeys.Contains(i.Project)).ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(paths.SiteData)!);
        File.WriteAllText(paths.SiteData, JsonSerializer.Serialize(data, Json.Options), new UTF8Encoding(false));
        Log.Info($"Prepared {published.Count} projects ({published.Count(p => p.Listed)} listed, {data.Developers} developers), " +
                 $"{data.Issues.Count} help-wanted issues, trend {(trend.HasHistory ? $"since {data.Trend.Baseline}" : "not available yet")} → {paths.SiteData}");
    }

    // Spotlight = manually featured projects, topped up with a weekly rotation of
    // active, well-starred ones. The site maintainer's own projects are never
    // picked, even if marked featured, so the person running the site can't
    // promote their own work. They still appear in every normal listing.
    static List<SiteProject> PickSpotlight(List<SiteProject> projects, DateTimeOffset now)
    {
        var eligible = projects.Where(p => p.Listed && !p.Maintainer && !p.Archived).ToList();
        var picks = eligible.Where(p => p.Featured).OrderByDescending(p => p.Stars ?? 0).Take(SpotlightSize).ToList();
        var week = $"{ISOWeek.GetYear(now.UtcDateTime)}-W{ISOWeek.GetWeekOfYear(now.UtcDateTime)}";
        picks.AddRange(eligible
            .Where(p => !p.Featured && p.Stars >= SpotlightMinStars && p.PushedAt >= now.AddDays(-90))
            .OrderBy(p => StableHash($"{week}:{p.Key}"))
            .Take(SpotlightSize - picks.Count));
        return picks;
    }

    // FNV-1a: the same week always picks the same projects, on any machine.
    static uint StableHash(string s)
    {
        var hash = 2166136261;
        foreach (var c in s) hash = (hash ^ c) * 16777619;
        return hash;
    }
}

sealed class SiteData
{
    public DateTimeOffset GeneratedAt { get; set; }
    public string SiteUrl { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string MaintainerLogin { get; set; } = "";
    public int ListingMinStars { get; set; }
    public SiteTrend Trend { get; set; } = new();
    public List<string> Categories { get; set; } = [];
    public int Developers { get; set; }
    public List<SiteProject> Projects { get; set; } = [];
    public List<string> NewThisMonth { get; set; } = [];
    public List<string> RecentlyActive { get; set; } = [];
    public List<string> Spotlight { get; set; } = [];
    public List<HelpWantedIssue> Issues { get; set; } = [];
}

sealed class SiteTrend
{
    public string Current { get; set; } = "";
    public string? Baseline { get; set; }
    public int WindowDays { get; set; }
}

sealed class SiteProject
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FullName { get; set; }
    public string Owner { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public string? DescriptionKa { get; set; }
    public string? Language { get; set; }
    public List<string> Topics { get; set; } = [];
    public int? Stars { get; set; }
    public int? Trend { get; set; }
    public int? HelpWanted { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string Category { get; set; } = "other";
    public bool Archived { get; set; }
    public bool Featured { get; set; }
    public bool Verified { get; set; }
    public bool Maintainer { get; set; }
    public bool Listed { get; set; }
    public string Source { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Review: the developers visitors will actually see, ranked by the stars of
// their listed repos, so curation effort goes where it matters. Output is
// Markdown on stdout; add unwanted logins to data/manual/developers.json.
// ---------------------------------------------------------------------------

static class Review
{
    public static void Run(Paths paths, Options options)
    {
        var config = Store.Read<DiscoveryConfig>(paths.DiscoveryConfig);
        var site = Store.Read<SiteConfig>(paths.SiteConfig);
        var developers = Store.ReadList<Developer>(paths.Developers).ToDictionary(d => d.Login, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        var listed = Store.ReadList<Project>(paths.Projects)
            .Where(p => p.Stars >= site.ListingMinStars)
            .Where(p => config.QualityBar.Passes(p.FullName, p.Description, p.IsFork, p.IsArchived, p.TemplateFrom, p.Stars, p.PushedAt, now))
            .GroupBy(p => p.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Owner: g.Key, Stars: g.Sum(p => p.Stars), Repos: g.OrderByDescending(p => p.Stars).ToList()))
            .OrderByDescending(x => x.Stars)
            .ToList();

        var top = options.Top ?? 200;
        var output = new StringBuilder();
        output.AppendLine($"# Developer review — top {Math.Min(top, listed.Count)} of {listed.Count}");
        output.AppendLine();
        output.AppendLine($"Developers with at least one listed repo (≥ {site.ListingMinStars} stars, passing the quality bar), ranked by total stars of those repos.");
        output.AppendLine("To remove someone, add their login to `exclude` in `data/manual/developers.json`.");
        output.AppendLine();
        output.AppendLine("| # | Login | Name | Location | Found by | ★ | Top repos |");
        output.AppendLine("|---|---|---|---|---|---|---|");
        var rank = 0;
        foreach (var (owner, stars, repos) in listed.Take(top))
        {
            developers.TryGetValue(owner, out var d);
            var topRepos = string.Join(", ", repos.Take(3).Select(r => $"[{r.FullName.Split('/')[1]}]({r.HtmlUrl}) {r.Stars}"));
            output.AppendLine($"| {++rank} | [{owner}](https://github.com/{owner}) | {Cell(d?.Name)} | {Cell(d?.Location)} | {Cell(d?.Source)} | {stars} | {topRepos} |");
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Write(output.ToString());
    }

    static string Cell(string? s) => s is null ? "" : s.Replace("|", "\\|").Replace("\n", " ");
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

sealed class SiteConfig
{
    public string SiteUrl { get; set; } = "";
    public string MaintainerLogin { get; set; } = "";
    public int TrendWindowDays { get; set; } = 30;
    public int TrendToleranceDays { get; set; } = 7;
    // Repos below this appear only in "Recently active" and search, not in listings.
    public int ListingMinStars { get; set; } = 10;
    public string RepoUrl { get; set; } = "";
}

sealed class HelpWantedIssue
{
    public string Project { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public List<string> Labels { get; set; } = [];
}

sealed class CategoryConfig
{
    public List<string> Order { get; set; } = [];
    public List<CategoryRule> Rules { get; set; } = [];
    public Dictionary<string, string> FallbackLanguages { get; set; } = [];

    // Strongest signal first: the owner's own topics, then the primary language, then
    // words in the name/description, then a language fallback. Within each pass the
    // first rule in config order wins. See config/categories.json.
    public string Infer(Project p)
    {
        var ignoreCase = StringComparer.OrdinalIgnoreCase;
        var text = $"{p.FullName.Split('/').Last().Replace('-', ' ').Replace('_', ' ')} {p.Description}";
        return Rules.FirstOrDefault(r => p.Topics is not null && p.Topics.Any(t => r.Topics.Contains(t, ignoreCase)))?.Category
            ?? Rules.FirstOrDefault(r => p.Language is not null && r.Languages.Contains(p.Language, ignoreCase))?.Category
            ?? Rules.FirstOrDefault(r => r.Words.Any(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))?.Category
            ?? (p.Language is not null && FallbackLanguages.TryGetValue(p.Language, out var fallback) ? fallback : null)
            ?? "other";
    }
}

sealed class CategoryRule
{
    public string Category { get; set; } = "";
    public List<string> Languages { get; set; } = [];
    public List<string> Topics { get; set; } = [];
    public List<string> Words { get; set; } = [];
}

sealed class DiscoveryConfig
{
    public List<string> Locations { get; set; } = [];
    public string SearchCreatedFrom { get; set; } = "2008-01-01";
    public int MinPublicRepos { get; set; } = 1;
    public QualityBar QualityBar { get; set; } = new();
    public List<string> HelpWantedLabels { get; set; } = [];
}

sealed class QualityBar
{
    public bool AllowForks { get; set; }
    // Archived repos are finished or retired projects; keep the well-starred ones.
    public int ArchivedMinStars { get; set; } = 10;
    public bool AllowTemplateGenerated { get; set; }
    public bool RequireDescription { get; set; } = true;
    public int MinStars { get; set; } = 3;
    public int ActiveWithinMonths { get; set; } = 12;
    // Case-insensitive regexes matched against the repo name and description,
    // to drop course exercises and test assignments.
    public List<string> ExcludePatterns { get; set; } = [];

    Regex[]? excludeRegexes;

    public bool Passes(string fullName, string? description, bool isFork, bool isArchived, string? templateFrom,
                       int stars, DateTimeOffset? pushedAt, DateTimeOffset now)
    {
        if (isFork && !AllowForks) return false;
        if (isArchived && stars < ArchivedMinStars) return false;
        if (templateFrom is not null && !AllowTemplateGenerated) return false;
        if (RequireDescription && string.IsNullOrWhiteSpace(description)) return false;
        if (IsExercise(fullName.Split('/').Last(), description)) return false;
        return stars >= MinStars || (pushedAt is { } p && p >= now.AddMonths(-ActiveWithinMonths));
    }

    bool IsExercise(string name, string? description)
    {
        excludeRegexes ??= ExcludePatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        return excludeRegexes.Any(r => r.IsMatch(name) || (description is not null && r.IsMatch(description)));
    }
}

// Checkpoint of an unfinished discovery run (data/bot/state/discovery.json).
sealed class DiscoveryState
{
    public DateTimeOffset StartedAt { get; set; }
    public string? Only { get; set; }
    public int? MaxUsers { get; set; }
    public Dictionary<string, string?> Candidates { get; set; } = [];  // login -> matched term
    public List<string> Fetched { get; set; } = [];                    // owners whose repos are done
    public List<string> Seen { get; set; } = [];                       // fetched owners with a listed repo
}

sealed class ManualDevelopers
{
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
}

sealed class OptOut
{
    public List<string> Developers { get; set; } = [];
    public List<string> Projects { get; set; } = [];
}

sealed class ManualProject
{
    public string? FullName { get; set; }
    public string? Url { get; set; }
    public string? Owner { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? DescriptionKa { get; set; }
    public string? Category { get; set; }
    public bool? Featured { get; set; }
    public bool? Verified { get; set; }
}

sealed class Developer
{
    public string Login { get; set; } = "";
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? Location { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string? MatchedTerm { get; set; }
    public string Source { get; set; } = "";
    public string FirstSeenAt { get; set; } = "";
}

sealed class Project
{
    public string Key { get; set; } = "";
    public long Id { get; set; }
    public string NodeId { get; set; } = "";
    public string FullName { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string? Description { get; set; }
    public string Owner { get; set; } = "";
    public string OwnerType { get; set; } = "";
    public int Stars { get; set; }
    public int? Forks { get; set; }
    public int? OpenIssues { get; set; }
    public int? HelpWantedIssues { get; set; }
    public string? Language { get; set; }
    public List<string>? Topics { get; set; }
    public bool IsFork { get; set; }
    public bool IsArchived { get; set; }
    public string? TemplateFrom { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string Source { get; set; } = "";
    public string FirstSeenAt { get; set; } = "";
    public string? RefreshedAt { get; set; }

    public static string KeyFor(long id) => $"gh:{id}";

    public void ApplyDiscovery(RepoSummary repo, string ownerType)
    {
        Id = repo.Id;
        NodeId = repo.NodeId;
        FullName = repo.FullName;
        HtmlUrl = repo.HtmlUrl;
        Description = repo.Description;
        Owner = repo.FullName.Split('/')[0];
        OwnerType = ownerType;
        Stars = repo.Stars;
        IsFork = repo.IsFork;
        IsArchived = repo.IsArchived;
        TemplateFrom = repo.TemplateFrom;
        PushedAt = repo.PushedAt;
    }

    public void ApplyRefresh(JsonNode repo, string today)
    {
        Id = repo["databaseId"]!.GetValue<long>();
        NodeId = repo["id"]!.GetValue<string>();
        FullName = repo["nameWithOwner"]!.GetValue<string>();
        HtmlUrl = repo["url"]!.GetValue<string>();
        Description = Json.NullIfBlank(repo["description"]?.GetValue<string>());
        Owner = repo["owner"]!["login"]!.GetValue<string>();
        OwnerType = OwnerTypeFrom(repo["owner"]!["__typename"]!.GetValue<string>());
        Stars = repo["stargazerCount"]!.GetValue<int>();
        Forks = repo["forkCount"]!.GetValue<int>();
        OpenIssues = repo["openIssues"]!["totalCount"]!.GetValue<int>();
        HelpWantedIssues = repo["helpWanted"]!["totalCount"]!.GetValue<int>();
        Language = repo["primaryLanguage"]?["name"]?.GetValue<string>();
        Topics = repo["repositoryTopics"]!["nodes"]!.AsArray().Select(t => t!["topic"]!["name"]!.GetValue<string>()).ToList();
        IsFork = repo["isFork"]!.GetValue<bool>();
        IsArchived = repo["isArchived"]!.GetValue<bool>();
        TemplateFrom = repo["templateRepository"]?["nameWithOwner"]?.GetValue<string>();
        PushedAt = Json.Date(repo["pushedAt"]);
        CreatedAt = Json.Date(repo["createdAt"]);
        RefreshedAt = today;
    }

    public static string OwnerTypeFrom(string typename) => typename == "Organization" ? "Organization" : "User";
}

sealed class Owner
{
    public string Login { get; init; } = "";
    public long Id { get; init; }
    public string Type { get; init; } = "";
    public string? Name { get; init; }
    public string? Location { get; init; }
    public string HtmlUrl { get; init; } = "";
    public List<RepoSummary> Repos { get; } = [];

    public static Owner From(JsonNode node) => new()
    {
        Login = node["login"]!.GetValue<string>(),
        Id = node["databaseId"]?.GetValue<long>() ?? 0,
        Type = Project.OwnerTypeFrom(node["__typename"]!.GetValue<string>()),
        Name = Json.NullIfBlank(node["name"]?.GetValue<string>()),
        Location = Json.NullIfBlank(node["location"]?.GetValue<string>()),
        HtmlUrl = node["url"]!.GetValue<string>(),
    };
}

sealed record RepoSummary(long Id, string NodeId, string FullName, string HtmlUrl, string? Description,
                          int Stars, bool IsFork, bool IsArchived, string? TemplateFrom, DateTimeOffset? PushedAt)
{
    public static RepoSummary From(JsonNode n) => new(
        n["databaseId"]!.GetValue<long>(),
        n["id"]!.GetValue<string>(),
        n["nameWithOwner"]!.GetValue<string>(),
        n["url"]!.GetValue<string>(),
        Json.NullIfBlank(n["description"]?.GetValue<string>()),
        n["stargazerCount"]!.GetValue<int>(),
        n["isFork"]!.GetValue<bool>(),
        n["isArchived"]!.GetValue<bool>(),
        n["templateRepository"]?["nameWithOwner"]?.GetValue<string>(),
        Json.Date(n["pushedAt"]));
}

// ---------------------------------------------------------------------------
// Storage: sorted, one record per line, UTF-8 without escaping Georgian.
// ---------------------------------------------------------------------------

sealed class Paths(string root)
{
    public string Root => root;
    public string DiscoveryConfig => Path.Combine(root, "config", "discovery.json");
    public string SiteConfig => Path.Combine(root, "config", "site.json");
    public string ManualDevelopers => Path.Combine(root, "data", "manual", "developers.json");
    public string ManualProjects => Path.Combine(root, "data", "manual", "projects.json");
    public string OptOut => Path.Combine(root, "data", "optout.json");
    public string BotData => Path.Combine(root, "data", "bot");
    public string Developers => Path.Combine(BotData, "discovered", "developers.json");
    public string Projects => Path.Combine(BotData, "discovered", "projects.json");
    public string Issues => Path.Combine(BotData, "discovered", "issues.json");
    public string CategoryConfig => Path.Combine(root, "config", "categories.json");
    public string SiteData => Path.Combine(root, "_build", "site-data.json");
    public string Snapshots => Path.Combine(BotData, "snapshots");
    public string DailySnapshots => Path.Combine(Snapshots, "daily");
    public string DiscoveryState => Path.Combine(BotData, "state", "discovery.json");
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static DateTimeOffset? Date(JsonNode? n) => n is null ? null : DateTimeOffset.Parse(n.GetValue<string>());
}

static class Store
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static T Read<T>(string path) where T : new() =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options) ?? new T() : new T();

    public static List<T> ReadList<T>(string path) => Read<List<T>>(path);

    public static void WriteObject<T>(string path, T value) =>
        Write(path, JsonSerializer.Serialize(value, Json.Indented) + "\n");

    public static void WriteList<T>(string path, IEnumerable<T> items)
    {
        var lines = items.Select(i => "  " + JsonSerializer.Serialize(i, Json.Options)).ToList();
        Write(path, lines.Count == 0 ? "[]\n" : "[\n" + string.Join(",\n", lines) + "\n]\n");
    }

    public static void WriteSnapshot(string path, string date, IEnumerable<Project> projects)
    {
        var lines = projects.OrderBy(p => p.Id).Select(p => $"    \"{p.Id}\": {p.Stars}").ToList();
        Write(path, $"{{\n  \"date\": \"{date}\",\n  \"stars\": {{\n{string.Join(",\n", lines)}\n  }}\n}}\n");
    }

    static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
    }
}

// ---------------------------------------------------------------------------
// GitHub GraphQL client with rate-limit handling.
// ---------------------------------------------------------------------------

sealed class GitHub : IDisposable
{
    // Search is limited far more tightly than other calls (~30/minute).
    static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2.1);
    const int LowWaterMark = 50;

    readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
    readonly Stopwatch sinceLastSearch = Stopwatch.StartNew();
    int requests;
    int? remaining;
    DateTimeOffset? resetAt;

    public GitHub(string token)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("git.ge-fetcher", "1.0"));
    }

    // GH_TOKEN for local development, GITHUB_TOKEN in Actions, else the GitHub CLI's login.
    public static string ResolveToken()
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } token)
                return token;
        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = gh.StandardOutput.ReadToEnd().Trim();
            gh.WaitForExit();
            if (gh.ExitCode == 0 && output.Length > 0) return output;
        }
        catch (System.ComponentModel.Win32Exception) { }
        throw new InvalidOperationException("No GitHub token: set GH_TOKEN, or log in with 'gh auth login'.");
    }

    public async Task<JsonNode> Search(string query, Dictionary<string, object?> variables)
    {
        var wait = SearchInterval - sinceLastSearch.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        try { return await GraphQL(query, variables); }
        finally { sinceLastSearch.Restart(); }
    }

    public async Task<JsonNode> GraphQL(string query, Dictionary<string, object?> variables)
    {
        var body = JsonSerializer.Serialize(new { query, variables }, Json.Options);
        for (var attempt = 1; ; attempt++)
        {
            await WaitIfLow();
            HttpResponseMessage response;
            string text;
            try
            {
                response = await http.PostAsync("graphql", new StringContent(body, Encoding.UTF8, "application/json"));
                text = await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException)
            {
                // Client-side timeout: the query is too heavy, same as a 502/504.
                if (attempt > 1) throw new GatewayTimeoutException(0);
                Log.Warn("  request timed out; retrying");
                continue;
            }
            using var _ = response;
            requests++;
            ReadRateLimit(response);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (attempt > 6) throw new HttpRequestException($"Rate limited too many times: {text}");
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (remaining == 0 && resetAt is { } r ? r - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60 * attempt));
                Log.Warn($"  rate limited ({(int)response.StatusCode}); waiting {delay.TotalSeconds:F0}s");
                await Task.Delay(delay);
                continue;
            }
            // GitHub sometimes answers a timed-out query with 200 and an empty body.
            if ((int)response.StatusCode >= 500 || (response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text)))
            {
                // Repeated 502/504 usually means the query is too heavy; let the caller shrink it.
                if (attempt > 1) throw new GatewayTimeoutException((int)response.StatusCode);
                Log.Warn($"  GitHub {(int)response.StatusCode}; retrying");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub returned {(int)response.StatusCode}: {text}");

            var json = JsonNode.Parse(text)!;
            var errors = json["errors"]?.AsArray() ?? [];
            if (errors.Any(e => e?["type"]?.GetValue<string>() == "RATE_LIMITED"))
            {
                var delay = resetAt is { } r ? r - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60);
                Log.Warn($"  GraphQL rate limit; waiting {delay.TotalSeconds:F0}s");
                await Task.Delay(delay);
                continue;
            }
            // NOT_FOUND is expected for renamed/deleted owners and repos; callers see a null node.
            foreach (var error in errors.Where(e => e?["type"]?.GetValue<string>() != "NOT_FOUND"))
                Log.Warn($"  GraphQL error: {error?.ToJsonString()}");
            return json["data"] ?? throw new HttpRequestException($"GraphQL returned no data: {text}");
        }
    }

    void ReadRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var r) && int.TryParse(r.First(), out var rem))
            remaining = rem;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var s) && long.TryParse(s.First(), out var epoch))
            resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    async Task WaitIfLow()
    {
        if (remaining is not { } rem || rem > LowWaterMark || resetAt is not { } reset) return;
        var delay = reset - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        if (delay <= TimeSpan.Zero) return;
        Log.Warn($"  {rem} GraphQL points left; waiting {delay.TotalMinutes:F1} min for reset");
        await Task.Delay(delay);
    }

    public void LogUsage() => Log.Info($"{requests} API requests; {remaining?.ToString() ?? "?"} GraphQL points left until {resetAt:HH:mm} UTC");

    public void Dispose() => http.Dispose();
}

sealed class GatewayTimeoutException(int status) : HttpRequestException($"GitHub returned {status} repeatedly");

sealed class Options
{
    public string? Only { get; private set; }
    public int? MaxUsers { get; private set; }
    public int? Top { get; private set; }
    public bool Restart { get; private set; }
    public bool Force { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--only": options.Only = args[++i]; break;
                case "--max-users": options.MaxUsers = int.Parse(args[++i]); break;
                case "--top": options.Top = int.Parse(args[++i]); break;
                case "--restart": options.Restart = true; break;
                case "--force": options.Force = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'");
            }
        }
        return options;
    }
}

// Logs "n / total" at most once a minute, so long runs show they are alive.
sealed class Progress(string what, int total)
{
    readonly Stopwatch sinceLast = Stopwatch.StartNew();
    int done;

    public void Advance(int count)
    {
        done += count;
        if (sinceLast.Elapsed < TimeSpan.FromMinutes(1) && done < total) return;
        Log.Info($"  {done} / {total} {what}");
        sinceLast.Restart();
    }
}

static class Log
{
    public static void Info(string message) => Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {message}");
    public static void Warn(string message) => Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} WARN {message}");
    public static void Error(string message) => Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} ERROR {message}");
}
