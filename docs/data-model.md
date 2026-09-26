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
  state/
    discovery.json        checkpoint of an unfinished discovery run (absent otherwise)
  spotlight.json          past Spotlight picks by ISO week (written by `prepare`)
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
  "templateFrom": null,
  "pushedAt": "2026-09-20T10:00:00+00:00",
  "createdAt": "2024-01-05T08:00:00+00:00",
  "source": "discovered",
  "firstSeenAt": "2026-09-25",
  "refreshedAt": "2026-09-25"
}
```

- `key` uses GitHub's numeric repo id, so renames and transfers keep their history.
- `openIssues` counts issues only (not pull requests).
- `templateFrom` is the template repo this one was generated from (GitHub Skills
  exercises, course starters), or `null`.
- `helpWantedIssues` = open issues carrying any label in
  `config/discovery.json` → `helpWantedLabels`.
- `source` is `"discovered"` (found via location search / include list) or
  `"submitted"` (only known because it is in `data/manual/projects.json`).

## Project — human record (`data/manual/projects.json`)

Keyed by `fullName` for GitHub projects (what a human knows), or by `url` for
projects hosted elsewhere. Only the fields being set need to be present.

```json
{ "fullName": "owner/repo", "descriptionKa": "…", "category": "tools", "featured": false, "verified": true }
{ "url": "https://gitlab.com/x/y", "owner": "x", "name": "y", "description": "…", "category": "libraries", "issue": 42 }
```

- `owner` (non-GitHub only) is the account name when the URL makes it clear
  (GitLab, Codeberg, Bitbucket, Gitea, sourcehut); opt-outs and the maintainer
  rule match against it.
- `issue` is the submission issue the entry came from, when added by
  `gitge.cs submit`; it makes re-running the same issue a no-op.
- Fields that aren't set are left out of the file rather than written as `null`.
  The file stays one record per line, sorted by `fullName`/`url`.

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

Configurable in `config/discovery.json` → `qualityBar`. A repo is admitted when
it is not a fork, not generated from a template, not archived (unless it has
at least `archivedMinStars` stars, default 10; the site badges it), has a
description (always required, at any star count), does not match any of `excludePatterns` (case-insensitive regexes
over name and description, aimed at homework, course exercises and test
assignments), and has **at least `minStars` stars (3)**, or is **new**: created
in the last `newRepoDays` (60) days with at least `newRepoMinStars` (1) star, so it
can appear under "New projects". Recent pushes alone don't count: most active
0-star repos are personal work in progress, and the site never showed them.
Curated and submitted projects bypass the bar. The nightly refresh drops
discovered repos that no longer meet the bar, based on their last known data; the
weekly discovery re-admits them if they grow.

## What is shown where

- **Category listings and the all-time sort** only show repos with at least
  `listingMinStars` stars (`config/site.json`, default 10).
- Admitted repos below that threshold appear only in **"Recently active" / "New
  this month"** and in search.

## Curation

`dotnet run gitge.cs -- review > review.md` writes a Markdown table of the
developers who have at least one listed repo, ranked by total stars. That is the
set visitors actually see, so it's the list worth reviewing by hand. Unwanted
accounts go into `exclude` in `data/manual/developers.json`.

## Spotlight

Three projects a week on the home page, chosen by `prepare`:

1. Projects marked `featured` in `data/manual/projects.json` come first.
2. The remaining slots rotate weekly among listed, non-archived projects with at
   least 20 stars and a push in the last 90 days, in an order derived from a hash
   of the week and the project key.

Rules: the maintainer's own projects are never picked, even when featured; at
most one pick per owner; a rotation pick doesn't return within 8 weeks; a week's
picks stay fixed once chosen. `spotlight.json` keeps a year of history for the
8-week rule.
