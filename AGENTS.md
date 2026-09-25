# AGENTS.md

Guidance for coding agents (Claude Code, Codex, Copilot, …) working on git.ge.
Humans: see README.md. The original brief is spec.md.

## What this is

A static site listing open-source projects by developers in Georgia. No backend:
GitHub is the storage layer. A C# fetcher writes JSON to the `data` branch, a
Razor-based generator renders static HTML, GitHub Actions run it nightly, and
Cloudflare Workers (static assets) serves it at https://git.ge.

## Layout

- `gitge.cs` — .NET 10 file-based app: discover, refresh, prune, prepare, review,
  submit, trend, selftest. Run from the repo root: `dotnet run gitge.cs -- <command>`.
- `site/` — renderer (Site.csproj, Razor components, `wwwroot/` CSS/JS/fonts).
  `dotnet run --project site` builds `_site/`; `-- serve` previews on localhost:5080.
- `config/` — discovery terms, quality bar, categories, site settings.
- `data/manual/`, `data/optout.json` — human-edited curation and opt-outs (on `main`).
- `data/bot/` — worktree of the orphan `data` branch (bot-written; git-ignored on main).
  Set up once: `git worktree add data/bot data`.
- `i18n/ka.json`, `i18n/en.json` — every UI string. `content/pages/` — About text.
  `content/roundups/` — roundup posts (Markdown + front matter).
- `.github/workflows/` — nightly, discover, deploy, submission, ci.
- `docs/data-model.md` — every file and field. Read it before changing data shapes.

## Before you finish a change

- `dotnet run gitge.cs -- selftest` passes.
- `dotnet build site` passes; for UI changes, `dotnet run gitge.cs -- prepare`
  then `dotnet run --project site -- serve` and look at the page.
- Workflows: lint with actionlint if available.

## Rules

- **Don't invent data.** If the API doesn't give a field, leave it null and show
  nothing. No guessed stars, dates or descriptions, and no AI-written text on the
  site that a human hasn't reviewed.
- **Ask before adding a dependency** beyond the .NET BCL, Markdig and the ASP.NET
  Core shared framework. The rendered site loads nothing from other domains
  (enforced by the CSP in `site/wwwroot/_headers`); no analytics, no embeds.
- **Opt-outs win everywhere**, including the published JSON. Never publish a
  developer's location.
- **The site must work without JavaScript.** JS only enhances (sort, search,
  language toggle).
- **The maintainer's own projects are never picked for the Spotlight.** Keep that
  rule in code (`Prepare.PickSpotlight`) and tested.
- **Bot data vs human data:** the fetcher writes only under `data/bot/` (the `data`
  branch); people edit `data/manual/`, `data/optout.json` and `config/`. Don't mix.
- **Host-specific code lives only in `.github/workflows/deploy.yml`.**
- Small commits with clear messages. Don't push the `data` branch by hand unless
  asked; CI owns it.

## Georgian text

Georgian first, English second. UI strings go in `i18n/`, never inline in
templates. The maintainer prefers native Georgian words over loanwords:

- location → ადგილმდებარეობა (not ლოკაცია)
- account / profile → ანგარიში (not პროფილი)
- label → ჭდე (not ლეიბლი); generates → ქმნის (not აგენერირებს)
- activity → ცვლილება (not აქტივობა)
- stored in the browser → ბრაუზერის მეხსიერებაში (no ლოკალურ)

Other loanwords (რეპოზიტორია, ტრენდი, დეველოპერი, …) were reviewed and kept for
now; ask before replacing them. Don't use `text-transform: uppercase` on Georgian
text (it turns into Mtavruli).
