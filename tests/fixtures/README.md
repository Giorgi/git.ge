# Snapshot fixtures

Used by `dotnet run gitge.cs -- selftest`. The latest snapshot is 2026-06-30, so the
30-day trend target is 2026-05-31 and the 90-day prune cutoff is 2026-04-01.

| repo | 2026-06-01 (baseline) | 2026-06-30 (latest) | expected trend |
|------|----------------------:|--------------------:|----------------|
| 1    | 10                    | 25                  | +15            |
| 2    | —                     | 7                   | null (new)     |
| 3    | 50                    | 48                  | −2             |
| 4    | 5                     | —                   | not reported (gone) |
| 5    | 100                   | 100                 | 0              |

2026-05-25 is a decoy: 36 days back, so further from the target than 2026-06-01.
February and March end before the cutoff and are rolled up into monthly files;
April is kept. `monthly/2026-01.json` exists already and must be left alone.
