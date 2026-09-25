# Roundups

A roundup covers one finished calendar month and is published once. Each is a
pair of Markdown files:

- `YYYY-MM.ka.md` (required): the Georgian version, shown by default.
- `YYYY-MM.en.md` (optional): the English version, shown by the English toggle.
  If it's missing, the Georgian text is used for both.

The URL is `/roundups/YYYY-MM/`. Start each file with:

```
---
title: ოქტომბერი 2026
date: 2026-11-01
summary: One line, shown in the list and the RSS feed.
---
```

To start one, generate the factual skeleton from the data:

    dotnet run gitge.cs -- roundup --month 2026-10

It lists the most popular repos created that month, the biggest star gains over
the month, and releases, and marks every place that needs prose with `TODO:`.
Fill those in, make sure no `TODO:` is left, and commit both files. The RSS
feed uses the Georgian version. Raw HTML is disabled; use Markdown only.
This README is not published.
