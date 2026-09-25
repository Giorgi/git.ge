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

## Removal

Anyone can ask to be removed by opening an issue or a PR that adds their login (or
`owner/repo`) to [`data/optout.json`](data/optout.json). Opt-out beats everything
else. The next refresh drops the entries from the data and from the site.

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

Commit the results in the worktree (`cd data/bot && git add -A && git commit`),
not on `main`.

## What's manual

- Georgian descriptions (`descriptionKa`), categories, `featured` and `verified`
  are set by hand in `data/manual/projects.json`.
- The include/exclude lists are maintained by hand.
- Roundup posts are written by hand in `content/roundups/`.
