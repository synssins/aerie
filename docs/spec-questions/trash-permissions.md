# Who sees and manages trashed content

- **Building:** `getContent` and `listContent` with `status=trashed` or `any`, `deleteContent` with `status=trashed`, `updateContent` restoring trashed content, `listSpaceTrash`, `emptySpaceTrash`.

## Question

`spaces.md` says the trash operations need "space administration permission". `content.md` does not say who may read trashed content. In aerie the recycle bin of a space belongs to those with its bin right (Supervisor and up), not only to those who administer the space (Owner). Which permission should these operations use?

## What aerie does until answered

Aerie's own rule: whoever has the bin right of the space reads trashed content, lists and empties the trash, purges and restores. Anyone else gets `404` for trashed content (it is not visible to them) and `403` for the space's trash.

See `src/Kb.Web/DataCenter/DcContentService.cs` and `src/Kb.Web/Actions/DataCenterContent.cs`.
