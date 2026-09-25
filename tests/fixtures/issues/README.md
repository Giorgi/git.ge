# Issue fixtures

Issue bodies as GitHub renders the forms in `.github/ISSUE_TEMPLATE/`, used by
`dotnet run gitge.cs -- selftest` (offline, against temporary copies of the data
files).

| file | type | expected |
|------|------|----------|
| submit-github.md | submission | added as `Giorgi/DuckDB.NET` (URL with `.git/` normalised), category dotnet, Georgian description |
| submit-other.md | submission | non-GitHub entry: URL lower-cased host, no trailing slash, owner `someone` |
| submit-duplicate.md | submission | rejected: `example/already-listed` is seeded as already listed |
| submit-invalid-url.md | submission | rejected: not a URL (a bare `host/path` would be accepted as https) |
| remove-login.md | removal | `SomeUser` (leading `@` dropped) added to `developers` |
| remove-repo.md | removal | `someone/project` (from a full URL) added to `projects` |
