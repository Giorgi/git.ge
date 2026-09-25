# About git.ge

git.ge lists open-source projects by developers in Georgia. It is a static site
with no backend: every night a scheduled job reads public data from the GitHub API,
and the pages are regenerated from it.

## Who is listed {#who}

- Developers whose GitHub profile location names a Georgian city (Tbilisi, Batumi,
  Kutaisi, …) in Latin or Georgian script. The word "Georgia" on its own isn't
  used, because it mostly matches the US state.
- Developers added by hand, for example people who live abroad or don't set a
  location.

Location is self-reported, so the list is neither complete nor exact. Accounts that
don't belong are excluded by hand.

## Which projects {#projects}

Public repositories owned by those developers that aren't forks and have a
description. Category pages and the full list show projects with at least
{{listingMinStars}} stars. Smaller active projects appear under "New projects",
"Recently active" and in search. Categories are inferred from a project's topics,
language and description, and can be corrected by hand.

## Trending {#trending}

The trend is the number of stars a project gained over roughly the last
{{trendWindowDays}} days, measured against a daily snapshot. New projects have no
trend yet ("—"). Until enough history has built up, lists are sorted by stars.

## The maintainer's projects {#maintainer}

The site is maintained by [@{{maintainerLogin}}](https://github.com/{{maintainerLogin}}).
Their own projects are never picked for the Spotlight. They appear in the normal
listings and sorts like everyone else's. This rule is enforced in the site's code.

## Removal {#removal}

Don't want to be listed? Fill in the [removal form]({{repoUrl}}/issues/new?template=remove.yml)
with your GitHub login (for your whole account) or `owner/repo` (for a single
project). You can also send a pull request that adds it to `data/optout.json`.
Once the request is accepted, removal takes effect with the next nightly update
and applies everywhere, including the site's data files.

## Suggesting a project {#submit}

Projects are found automatically, but you can also
[suggest one]({{repoUrl}}/issues/new?template=submit-project.yml), for example if
you don't set a location on GitHub or the project isn't hosted on GitHub. Every
suggestion is reviewed by hand before it's added.

## Privacy {#privacy}

No cookies, no tracking and no ads. We count visits with Cloudflare Web Analytics,
which works without cookies: it records which page was viewed, where the visit came
from, the browser type and the country, and shows them only as totals. It builds no
personal profiles and doesn't follow you across sites. If you switch to English,
that choice is stored only in your browser's local storage.

## How it was built {#built-with}

The whole project, from the data fetcher and the site generator to the automation, was built with AI: [Claude Code](https://claude.com/claude-code) with the Claude Opus 5.5 model. Decisions about what the site does and the rules it follows were made by the maintainer.

## Data {#data}

Data comes from GitHub's public API. Stars, languages and dates are shown as GitHub
reports them. The code and the data are public in [the repository]({{repoUrl}}).
