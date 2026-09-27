# Spec: developer pages (`/u/<login>/`)

Status: v1 built. Owner: maintainer. Last updated 2026-09-27.

## Why

A GitHub profile already lists a developer's repos. A git.ge page shows only what
GitHub can't:

- their work **filtered** to what passes git.ge's quality bar, with categories;
- **category ranks** ("#1 in .NET, of 53");
- their place among **git.ge developers**: who follows them and whom they follow
  on GitHub, and contributions between git.ge projects;
- **recognition** from git.ge itself (Spotlight weeks, roundups);
- **a way in for contributors** (their open beginner-friendly issues).

## Non-goals

- No location, ever.
- No global leaderboard of people. Ranks are only within a category, and the
  directory has no rank numbers.
- No data GitHub doesn't provide, and nothing inferred about a person.
- No stargazers: GitHub only shows the list of who starred a repo to its owner.

## Who gets a page

Every owner (user or organization) with **at least one listed project** (10+
stars, or curated/submitted). Excluded (`data/manual/developers.json`) and
opted-out (`data/optout.json`) developers get no page and never appear on anyone
else's page, in either direction: their projects never reach the published data,
and every list on a page only contains people who have a page.

URL: `/u/<login>/`, with the login lower-cased. A renamed login's old URL returns 404.

## Page content

In this order; any section without content is left out.

1. **Header:** avatar (96 px), name (or login), `github.com/<login>`, person or
   organization, and a summary: "N projects · M★ · languages · categories".
2. **Chips:**
   - Category ranks: the developer's listed projects that rank within
     `rankTopN` (10) in their category by stars, e.g. "#1 .NET of 53" plus the
     project name.
   - Spotlight weeks.
   - Roundups that link to one of their projects.
3. **git.ge developers on GitHub:** two rows, "Followed on GitHub by N developers
   from git.ge" and "Follows M developers from git.ge". Each has up to 12 avatars
   and then "and K more". Mutual follows are listed first, with a green ring, and a
   note explains the ring. A row with 0 is hidden.
4. **Projects:** their listed projects as cards. On this page the card shows the
   category instead of the owner. Smaller (unlisted but published) projects are
   in a collapsed `<details>`.
5. **Side column:**
   - "Contributes to git.ge projects": projects owned by someone else where they
     have at least `minCommits` commits.
   - "Contributors from git.ge": developers with pages who contributed to their
     projects, with commit counts.
   - "Help wanted": up to 5 of their open good-first/help-wanted issues.

Everywhere else, the owner part of a project card (`owner/`) links to `/u/<owner>/`
when the owner has a page, otherwise to their GitHub profile. That covers every
list, and search results too, via `"w": 1` in `index.json`.

## Directory (`/u/`)

- Everyone with a page, in a compact grid: avatar, name, login, number of listed
  projects, and top categories.
- **A–Z by default,** with sort buttons (app.js): A–Z, projects, followers from
  git.ge, recent activity, and new on git.ge. There are no rank numbers.
- Works without JavaScript (A–Z).
- In the main menu as "დეველოპერები / Developers".

## Data

### Weekly fetch: `gitge.cs social` (runs in `discover.yml`)

- **`data/bot/discovered/contributors.json`,** one line per listed GitHub project:
  - `{ project, fullName, fetchedAt, contributors: [ { login, commits } ] }`
  - From REST `GET /repos/{owner}/{repo}/contributors` (top 100, all time).
  - Only `type: "User"`; `[bot]` logins and known bots are skipped, and anonymous
    contributors aren't requested.
- **`data/bot/discovered/follows.json`,** one line per developer with a page:
  - `{ login, fetchedAt, followerCount, followingCount, truncated, followers, following }`
  - From REST `followers` and `following`, up to 30 pages each (3,000).
  - `followers` and `following` keep only git.ge developers (discovered or
    included, not excluded or opted out); the counts are totals as fetched.
- **Conditional requests:** ETags are stored in `data/bot/state/contributors-etags.json`
  and `follows-etags.json`. Unchanged lists answer 304, which doesn't count
  against the rate limit.
- **Resumable:** entries with `fetchedAt` = today are skipped unless `--force`.
  The files are saved about once a minute.
- **Access refused** (for example an enterprise blocking the token) keeps the
  previous data. Not found drops the entry.

### Avatars: `gitge.cs avatars` (runs before every site build)

- Downloads `https://avatars.githubusercontent.com/u/<id>?s=96` for each developer
  with a page into the avatar cache: `_cache/avatars/<login>.<ext>`, or
  `$GITGE_AVATARS`.
- Only files that are missing or more than 7 days old are downloaded.
- Files of people without a page are deleted, so opting out removes the image.
- The cache is never committed. CI keeps it with `actions/cache`, keyed by ISO week.
- The site build copies cached files to `_site/avatars/`. People without a cached
  image get an initial in a neutral circle, done in CSS, so the CSP stays
  `img-src 'self'`.

### Computed in `prepare`

`site-data.json` → `developerPages`: one entry per page with `login, name, type,
url, projects, smaller, stars, languages, categories, helpWanted, firstSeenAt,
lastPush, ranks, spotlight, roundups, contributesTo, contributors, followedBy,
follows, mutual`. This is built by `DeveloperPages.Build`, a pure function with
self-tests.

### Config (`config/site.json`)

```json
"developerPages": { "minCommits": 1, "rankTopN": 10 }
```

`minCommits` is 1 because the network is sparse. About 37 of 434 developers have
any contribution link to another git.ge developer, so a higher threshold would
hide the section almost everywhere.

## Tests

`selftest` → "Developer pages":
- only owners with listed projects get pages
- own projects never count as "contributes to"
- contributors and follows only include people with pages
- `minCommits` is applied
- mutual follows are found and listed first
- category ranks respect `rankTopN`
- Spotlight weeks
- roundup mentions match the exact repo
- an opted-out developer appears nowhere, in either direction
- bots are recognised

CI builds the site and checks that `/u/` and the first developer page in the
directory exist.

## Later

- Star momentum, once trend data exists (from mid-October 2026).
- A README badge (`/u/<login>/badge.svg`).
- Short addresses (`git.ge/<login>` → `/u/<login>/`), where no site path collides.
- "Claim your page": a Georgian and English bio, links, and an "open to work" flag.
