# git.ge

A static directory of open-source projects by developers in Georgia. There is no
backend: GitHub is the storage layer, a scheduled GitHub Action fetches data and
commits it, and the site is regenerated as static HTML.

Status: **work in progress.** The fetcher works. The site generator, submission
forms and workflows are not built yet (see `spec.md` for the build order).

## How data flows

```
GitHub API ──► gitge.cs discover (weekly) ──► data/bot/discovered/*.json ─┐
           └─► gitge.cs refresh  (nightly) ──► data/bot/snapshots/daily/  ├─► site generator ──► static HTML
config/ + data/manual/ + data/optout.json  (edited by humans, via PR) ────┘
```

- **Bot data** is committed to the orphan `data` branch. Humans don't edit it.
- **Human data** (config, curation, opt-outs, roundups) lives on `main`.

See [`docs/data-model.md`](docs/data-model.md) for every file and field.

## Who gets listed

A developer is included if their GitHub profile location contains one of the
Georgian city names in [`config/discovery.json`](config/discovery.json). The bare
word "Georgia" is never searched, because it mostly matches the US state.

What this means in practice, honestly:

- **Location is self-reported and free text.** People who don't set it, or who
  live abroad, are missed. Add them to `include` in
  [`data/manual/developers.json`](data/manual/developers.json).
- **Living in Georgia isn't the same as being Georgian**, and many people who
  moved to Tbilisi or Batumi are matched. That is deliberate, not a bug. Use
  `exclude` for accounts that shouldn't be listed (wrong match, bots, etc.).
- Only repos **owned** by the developer or org are found, not contributions to
  other repos.
- Auto-discovered repos must pass a quality bar: not a fork, has a description,
  not archived unless it has 10+ stars, and (≥ 3 stars or pushed in the last 12
  months). Repos without a description are never listed, whatever their stars.

## Submissions and removals

Two issue forms live in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/):
**Suggest a project** (label `submission`) and **Request removal** (label
`removal`). Nothing is added or removed without the maintainer's review:

1. Someone fills in a form. GitHub opens an issue with the `submission` or
   `removal` label.
2. The maintainer reads it and, if it's fine, adds the label `accepted`.
3. A workflow runs `dotnet run gitge.cs -- submit --issue <n>`:
   - A submission is added to `data/manual/projects.json`. GitHub URLs are
     normalised to `owner/repo` and checked: the repo must exist, be public, not be
     a fork and have a description. Duplicates and opted-out or excluded owners
     are rejected.
   - A removal adds the login or `owner/repo` to `data/optout.json`.
4. If it succeeds (exit 0), the workflow opens a pull request that closes the
   issue. If it's rejected (exit 3), the workflow posts the bilingual explanation
   as a comment on the issue instead.
5. The maintainer merges the pull request.

Each added entry records the issue it came from (`"issue": 42`), so running the
same issue twice changes nothing. To try it locally without GitHub:

```sh
dotnet run gitge.cs -- submit --body-file tests/fixtures/issues/submit-github.md --type submission --offline
```

(This edits `data/manual/projects.json`; revert it afterwards.) If you rename a
field label in a form, update `SubmissionForm`/`RemovalForm` in `gitge.cs`: they
match headings by the English part of the label.

## Running the fetcher locally

Requirements: .NET 10 SDK. For a token, either set `GH_TOKEN`, or be logged in
with the GitHub CLI (`gh auth login`); the fetcher falls back to `gh auth token`.

```sh
# once: check out the data branch where bot data lives
git worktree add data/bot data

# find developers and their repos (a full run takes about 30-60 minutes)
dotnet run gitge.cs -- discover

# faster, for development: one location term, or a capped number of users
dotnet run gitge.cs -- discover --only Batumi
dotnet run gitge.cs -- discover --only Tbilisi --max-users 200

# update stats for every known repo and write today's star snapshot
dotnet run gitge.cs -- refresh
```

A partial run (`--only` or `--max-users`) only adds and updates. Only a full run
removes developers who are no longer found.

**Both commands resume after a crash or timeout.** Just run the same command
again:

- `discover` saves a checkpoint to `data/bot/state/discovery.json` about once a
  minute: the candidate list from the search, and which owners are done. A rerun
  with the same options skips the search and the finished owners. The checkpoint
  is deleted when the run completes. Pass `--restart` to throw it away. A
  checkpoint older than 14 days is ignored.
- `refresh` saves `projects.json` about once a minute and skips repos already
  refreshed today (UTC). Pass `--force` to refresh everything again.

### Trending and snapshots

```sh
dotnet run gitge.cs -- trend      # current trend window and top gainers
dotnet run gitge.cs -- prune      # roll daily snapshots older than ~90 days into monthly ones
dotnet run gitge.cs -- selftest   # test trending and pruning against tests/fixtures/
```

Trend = stars gained since the snapshot closest to 30 days before the latest one
(anywhere from 23 to 37 days back). Until such a snapshot exists, there is no
trend and the site sorts by stars instead. A repo missing from that older snapshot
shows "—", not its whole star count.

Commit the results in the worktree (`cd data/bot && git add -A && git commit`),
not on `main`.

## What's manual

- Georgian descriptions (`descriptionKa`), categories, `featured` and `verified`
  are set by hand in `data/manual/projects.json`.
- The include/exclude lists are maintained by hand.
- Roundup posts are written by hand in `content/roundups/`.

## Automation

GitHub Actions runs everything; the workflows are in `.github/workflows/`.

| Workflow | When | What it does |
|---|---|---|
| `nightly.yml` | daily, 02:17 UTC | `refresh` → `prune` → `prepare`, commits bot data to the `data` branch, builds the site, deploys it |
| `discover.yml` | Mondays 03:23 UTC; daily 04:41 UTC only to resume | full `discover`, then `refresh` for the new repos, commits bot data |
| `deploy.yml` | called by `nightly.yml`; or run by hand | publishes `_site/` to Cloudflare Pages. The only file that knows the host |
| `submission.yml` | an issue labelled `submission` or `removal` gets the `accepted` label | runs `gitge.cs submit` and opens a PR, or comments why the issue was rejected |
| `ci.yml` | pushes to `main`, pull requests | `selftest`, builds the site project, builds the whole site from the `data` branch |

`nightly.yml` and `discover.yml` share a concurrency group, so they never write
the `data` branch at the same time. Both commit their progress even when a step
fails, so a rerun resumes instead of starting over.

### Settings to configure

Secrets (Settings → Secrets and variables → Actions):

- `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`: for deploys. Until they are
  set, `deploy.yml` skips the deploy with a warning instead of failing.
- `GITGE_TOKEN` (optional): a fine-grained personal access token with read-only
  access to public repositories. The default `GITHUB_TOKEN` is limited to about
  1,000 API requests an hour per repository; a personal token gets about 5,000.
  The nightly refresh fits either way. A full weekly discovery takes about 35
  minutes with a personal token and several hours without one; thanks to
  checkpoints it finishes either way, just over more runs.

Variables:

- `CF_PAGES_PROJECT` (optional): the Cloudflare Pages project name, default
  `git-ge`.

Repository settings:

- Settings → Actions → General → Workflow permissions: enable **"Allow GitHub
  Actions to create and approve pull requests"**, or `submission.yml` can't open
  PRs.
- The `data` branch must exist on GitHub (push it once:
  `git push origin data`).

### Honest notes

- Scheduled runs can start late, sometimes by an hour or more, when GitHub is
  busy. Nothing depends on exact timing: snapshots are dated by the UTC day they
  run on.
- GitHub disables scheduled workflows in a public repository after 60 days
  without activity. The nightly data commits count as activity, so this only
  happens if the nightly run itself keeps failing.
- Pull requests opened by `submission.yml` use `GITHUB_TOKEN`, and GitHub doesn't
  run workflows for events created by that token. So `ci.yml` doesn't run on
  those PRs automatically; close and reopen the PR (or push to it) to trigger it.
  The change is a small edit to one JSON file, and CI runs again on `main` after
  the merge.
