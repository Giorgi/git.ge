# Data model and on-disk layout

## Principle: bot data and human data never share a file

Everything the fetcher writes lives on the orphan **`data` branch**. Everything a
person edits (config, curation, opt-outs, roundups) lives on **`main`**. Nightly
commits therefore never conflict with contributors' pull requests, and `main`'s
history stays readable. The `data` branch can be squashed later if it grows too
large; `main` is never rewritten.

Locally the `data` branch is checked out as a worktree at `data/bot/`, which is
git-ignored on `main`:

    git worktree add data/bot data

## Layout

```
main branch
  config/
    discovery.json        location search terms, quality bar, help-wanted labels
    site.json             site URL, maintainer login, trend window
  data/
    manual/
      developers.json     { "include": [logins], "exclude": [logins] }
      projects.json       submitted projects + curation of discovered ones
    optout.json           { "developers": [logins], "projects": [owner/repo] }
  content/roundups/*.md
  i18n/ka.json, en.json

data branch (worktree at data/bot/)
  discovered/
    developers.json
    projects.json
  snapshots/
    daily/YYYY-MM-DD.json
    monthly/YYYY-MM.json
```

**One file per entity type, one record per line, sorted by key.** A changed star
count is a one-line diff, the same as with per-record files, without thousands of
tiny files. The browser never reads these: the site generator emits a separate
public `index.json` (hidden/opted-out removed, no locations), which is a build
output and is not committed.

## Who is included

A developer is included when **either**:

- their GitHub profile location contains one of the terms in
  `config/discovery.json` (Georgian city names in Latin and Georgian script, plus
  "საქართველო"/"Sakartvelo"). The bare string "Georgia" is never searched: it
  mostly matches the US state; **or**
- their login is in `data/manual/developers.json` → `include`.

And **neither**:

- their login is in `data/manual/developers.json` → `exclude` (curation: wrong
  match, not a developer, etc.), **nor**
- their login is in `data/optout.json` → `developers` (removal on request).

`optout.json` wins over everything, including `include` and manual project entries.

## Project — bot record (`discovered/projects.json`)

GitHub fields only. `null` means "unknown / not fetched", never a guess.

```json
{
  "key": "gh:123456789",
  "id": 123456789,
  "nodeId": "R_kgDO...",
  "fullName": "owner/repo",
  "htmlUrl": "https://github.com/owner/repo",
  "description": "…",
  "owner": "owner",
  "ownerType": "User",
  "stars": 42,
  "forks": 3,
  "openIssues": 5,
  "helpWantedIssues": 2,
  "language": "C#",
  "topics": ["dotnet", "cli"],
  "isFork": false,
  "isArchived": false,
  "pushedAt": "2026-09-20T10:00:00+00:00",
  "createdAt": "2024-01-05T08:00:00+00:00",
  "source": "discovered",
  "firstSeenAt": "2026-09-25",
  "refreshedAt": "2026-09-25"
}
```

- `key` uses GitHub's numeric repo id, so renames and transfers keep their history.
- `openIssues` counts issues only (not pull requests).
- `helpWantedIssues` = open issues carrying any label in
  `config/discovery.json` → `helpWantedLabels`.
- `source` is `"discovered"` (found via location search / include list) or
  `"submitted"` (only known because it is in `data/manual/projects.json`).

## Project — human record (`data/manual/projects.json`)

Keyed by `fullName` for GitHub projects (what a human knows), or by `url` for
projects hosted elsewhere. Only the fields being set need to be present.

```json
{ "fullName": "owner/repo", "descriptionKa": "…", "category": "tools", "featured": false, "verified": true }
{ "url": "https://gitlab.com/x/y", "name": "y", "description": "…", "category": "libraries" }
```

The generator merges the two: manual fields win; `hidden` comes from
`optout.json`; `category` is manual if set, otherwise inferred from
`config/categories.json`. Non-GitHub projects have `null` stats.

## Developer — bot record (`discovered/developers.json`)

Only developers with at least one listed project are stored.

```json
{
  "login": "…", "id": 98765, "type": "User", "name": "…",
  "location": "Tbilisi, Georgia", "htmlUrl": "https://github.com/…",
  "matchedTerm": "Tbilisi", "source": "search", "firstSeenAt": "2026-09-25"
}
```

`location` is used for discovery and review only; it is never published in the
site's JSON. `source` is `"search"` or `"include"`.

## Snapshot (`snapshots/daily/YYYY-MM-DD.json`)

```json
{ "date": "2026-09-25", "stars": { "123456789": 42, "…": 7 } }
```

Keyed by repo id, one entry per line. Written by every nightly refresh (a second
run on the same UTC day overwrites).

- **Trend** = today's stars − stars in the snapshot closest to 30 days ago, taken
  from anywhere between 23 and 37 days back. No such snapshot, or the repo is
  absent from it → trend is `null`, shown as "—".
- **Cold start:** until a snapshot at least 23 days old exists, the default sort
  is all-time stars and recent activity, and the site says trend data is still
  accumulating.
- **Roll-up:** daily files older than 90 days are replaced by
  `monthly/YYYY-MM.json`, which is the last daily snapshot of that month.

## Quality bar (auto-discovered repos only)

Defaults, configurable in `config/discovery.json`: not a fork, not archived, has
a description, and (≥ 3 stars **or** pushed within the last 12 months). Submitted
projects bypass the bar. A repo that later drops below the bar stays in the data
file and is filtered at site-generation time, so the list doesn't flicker.
