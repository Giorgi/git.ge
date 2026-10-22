# Roundups

A roundup covers one finished calendar month and is published once, between the
15th and 25th of the following month (aim for the 20th). Waiting gives repos
created late in the month time to collect stars, and discovery time to find
them. Stars and counts in a roundup are as of the day it was generated, and it
says so. Each is a pair of Markdown files:

- `YYYY-MM.ka.md` (required): the Georgian version, shown by default.
- `YYYY-MM.en.md` (optional): the English version, shown by the English toggle.
  If it's missing, the Georgian text is used for both.

The URL is `/roundups/YYYY-MM/`. Start each file with:

```
---
title: ოქტომბერი 2026
date: 2026-11-20
summary: One line, shown in the list and the RSS feed.
---
```

The title is the month covered; `date` is the day it was generated and published.

To start one, generate the factual skeleton from the data:

    dotnet run gitge.cs -- roundup --month 2026-10   # on 2026-11-15…25

It lists the most popular repos created that month, the biggest star gains over
the month, and releases, and marks every place that needs prose with `TODO:`.
Fill those in, make sure no `TODO:` is left, and commit both files. The RSS
feed uses the Georgian version. Raw HTML is disabled; use Markdown only.
This README is not published.
