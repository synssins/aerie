# Title length

- **Building:** `createContent` and `updateContent`.

## Question

The contract allows titles of up to 255 characters. Aerie's page titles are at most 200. Should aerie widen its titles to 255?

## What aerie does until answered

A title longer than 200 characters gives `400`. See `src/Kb.Web/Actions/DataCenterContent.cs`.
