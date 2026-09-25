#!/usr/bin/env dotnet
#:property PublishAot=false
#:property Nullable=enable

// git.ge tooling. Run from the repository root:
//   dotnet run gitge.cs -- discover [--only <term>] [--max-users <n>] [--restart]
//   dotnet run gitge.cs -- refresh [--force]
//   dotnet run gitge.cs -- review [--top <n>] > review.md
// See docs/data-model.md for the files these commands read and write.

using System.Diagnostics;
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
        case "review":
            Review.Run(paths, options);
            return 0;
        default:
            Console.Error.WriteLine("usage: dotnet run gitge.cs -- <discover|refresh|review> [options]");
            Console.Error.WriteLine("  discover [--only <location term>] [--max-users <n>] [--restart]");
            Console.Error.WriteLine("  refresh [--force]    --force re-fetches repos already refreshed today");
            Console.Error.WriteLine("  review [--top <n>]   Markdown list of the most visible developers, for curation");
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
        Log.Info($"Refresh done: {updated} updated, {removed} removed, {toResolve.Count} submitted looked up; snapshot {today} written");
        github.LogUsage();
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
    public string DiscoveryConfig => Path.Combine(root, "config", "discovery.json");
    public string SiteConfig => Path.Combine(root, "config", "site.json");
    public string ManualDevelopers => Path.Combine(root, "data", "manual", "developers.json");
    public string ManualProjects => Path.Combine(root, "data", "manual", "projects.json");
    public string OptOut => Path.Combine(root, "data", "optout.json");
    public string BotData => Path.Combine(root, "data", "bot");
    public string Developers => Path.Combine(BotData, "discovered", "developers.json");
    public string Projects => Path.Combine(BotData, "discovered", "projects.json");
    public string DailySnapshots => Path.Combine(BotData, "snapshots", "daily");
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
