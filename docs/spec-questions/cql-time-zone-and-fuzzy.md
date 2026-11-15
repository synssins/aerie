# CQL dates and loose matching

- **Building:** `searchContent` and `search` (`contracts/behaviour/search-cql.md`).

## Question

1. Dates and the date functions are read "in the site's time zone", and weeks start on the site's first day of the week. Aerie has no site time zone setting. Which zone should it use?
2. A word followed by `~` is "matched loosely (spelling variations)". How loose?

## What aerie does until answered

1. Dates are read in UTC, and weeks start on Monday.
2. A loose word matches any word of the item at most one letter away (one letter added, removed or changed).

See `src/Kb.Web/DataCenter/Cql.cs`.
