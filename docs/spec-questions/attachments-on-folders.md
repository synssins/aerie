# Attachments on folders

- **Building:** `createAttachments`, `updateAttachment` and `moveAttachment` (`contracts/behaviour/attachments.md`).

## Question

Under the tree mapping of `content.md`, a folder shows as `type: page`, and a page that gains a child becomes a folder, keeping its attachments. Aerie itself keeps attachments on pages only: its own upload refuses a folder. May a client add new attachments to a folder, or move one onto a folder?

## What aerie does until answered

Attachments a page had when it became a folder stay, and are listed, read, versioned and removed as usual. Adding a new attachment to a folder, or moving one onto a folder, gives `400`. See `src/Kb.Web/Actions/DataCenterAttachments.cs`.
