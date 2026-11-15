# Label prefixes and aerie's tag limits

- **Building:** `listContentLabels`, `addContentLabels`, `removeContentLabelByQuery`, `removeContentLabel` (`contracts/behaviour/labels-properties.md`), and CQL `label`.

## Question

Labels map to aerie's page tags, which are shared by everyone (global), at most 50 characters each, and at most 20 on a page. The contract has `my` (personal) and `team` labels too, and allows names of 255 characters. Should aerie hold personal and team labels, and widen its tag limits?

## What aerie does until answered

- Only `global` labels exist. Adding a `my` or `team` label gives `400`; listing with `prefix=my` or `team` only gives an empty list; CQL `label = my:name` matches nothing.
- A label over 50 characters, or a 21st label on a page, gives `400` and adds none of the request's labels.

See `src/Kb.Web/Actions/DataCenterAttachments.cs` and `src/Kb.Web/DataCenter/Cql.cs`.
