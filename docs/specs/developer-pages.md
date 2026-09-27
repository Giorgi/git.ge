# Spec: developer pages (`/@<login>/`) and the directory (`/developers/`)

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

Every owner (user or organization) with **at least one published project**, listed
(10+ stars, curated or submitted) or search-only (3+ stars): 861 pages. Excluded (`data/manual/developers.json`) and
opted-out (`data/optout.json`) developers get no page and never appear on anyone
else's page, in either direction: their projects never reach the published data,
and every list on a page only contains people who have a page.

URL: `/@<login>/`, with the login lower-cased (e.g. `/@giorgi/`); the output folder is `_site/@giorgi/`. A renamed login's old URL returns 404.

## Page content

In this order; any section without content is left out.

1. **Header:** avatar (96 px), name (or login), `github.com/<login>` (opens in a
   new tab, `rel="noopener"`), person or organization, a summary ("N projects · M★ ·
   languages · categories"), and chips for Spotlight weeks and roundups.
2. **Category ranking:** one sentence per listed project that ranks within
   `rankTopN` (10) in its category by stars, project first:
   - ka: "EntityFramework.Exceptions: ვარსკვლავებით 1-ელი კატეგორიაში „.NET“ (55 პროექტიდან)"
   - en: "EntityFramework.Exceptions: 1st most-starred in .NET (of 55 projects)"

   Both languages are rendered and toggled with `.l10n`. The ordinals come from
   `Format.OrdinalKa` / `OrdinalEn`. Georgian follows grammar.emis.ge exercise 179:
   - 1 → 1-ელი
   - მე-N for 2–20, 40, 60, 80, 100–900 in whole hundreds, and 1000
   - N-ე otherwise

   These are checked by `dotnet run --project site -- selftest`.
3. **git.ge developers on GitHub:** three disjoint rows, each with a count, up to
   12 avatars and "and K more", hidden when empty:
   - "Follow each other" (mutual)
   - "Followed by" (followers who aren't mutual)
   - "Follows" (following who aren't mutual)
4. **Projects:** their listed projects as cards. On this page the card shows the
   category instead of the owner. Smaller (search-only) projects are in a
   collapsed `<details>`, or shown directly when the developer has no listed project.
5. **Side column,** with one row layout (avatar, text and note on one centered line):
   - "Contributes to git.ge projects": the project owner's avatar, `owner/`
     linking to their page, and the repo, where they have at least `minCommits`
     commits.
   - "Contributors from git.ge": developers with pages who contributed to their
     projects, with commit counts.
   - "Help wanted": up to 5 of their open good-first/help-wanted issues.

URLs from submissions (projects hosted outside GitHub) are linked with
`rel="nofollow ugc"` everywhere they appear. GitHub links get no `nofollow`.

Everywhere else, the owner part of a project card (`owner/`) links to `/@<owner>/`
when the owner has a page, otherwise to their GitHub profile. That covers every
list, and search results too, via `"w": 1` in `index.json`.

## Directory (`/developers/`)

- Everyone with a page, in a compact grid: avatar, name, login, number of
  projects, and top categories. Cards in a row share one height with aligned
  bottom borders. The meta line is one line with an ellipsis; app.js sets its full
  text as `title`.
- **Compact HTML** (about 860 cards): each card carries only the default meta line
  as plain Georgian text plus `data-projects`, `data-followed`, `data-pushed`,
  `data-first`, `data-stars` and `data-cats`. app.js builds the sort metric line,
  the English meta line and the tooltips from those. That brought `/developers/`
  from 1,135 KB to 442 KB raw, and from 56 KB to 36 KB gzipped.
- **Default order (and the no-JavaScript view):**
  - developers with a listed project, A–Z;
  - then "სხვა დეველოპერები / More developers" (search-only projects), A–Z.
- **Sort buttons (app.js):**
  - A–Z, projects, "Most followed on git.ge", recent activity, new on git.ge.
  - Any sort other than A–Z merges everyone into one list and hides the second
    heading. Each card then shows that sort's metric instead of the normal meta
    line: followers on git.ge, last change, or first seen.
  - A–Z restores the two groups.
  - There are no rank numbers.
- In the main menu as "დეველოპერები / Developers".

## Data

### Weekly fetch: `gitge.cs social` (runs in `discover.yml`)

- **`data/bot/discovered/contributors.json`,** one line per published GitHub project:
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

CI builds the site and checks that `/developers/` and the first developer page (`/@…/`) in the
directory exist.

## Later

- Star momentum, once trend data exists (from mid-October 2026).
- A README badge (`/@<login>/badge.svg`).
- Short addresses (`git.ge/<login>` → `/@<login>/`), where no site path collides.
- "Claim your page": a Georgian and English bio, links, and an "open to work" flag.
