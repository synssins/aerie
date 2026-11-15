# Removing a version, and purging from the trash

- **Building:** `DELETE /rest/api/content/{id}/version/{versionNumber}` (`deleteContentVersion`), `DELETE /rest/api/content/{id}?status=trashed` (`deleteContent`, purge) and `DELETE /rest/api/space/{spaceKey}/trash` (`emptySpaceTrash`).

## Question

`content.md` says these remove a version, or trashed content, "for good". Aerie's history is append-only, and its recycle bin is emptied by hiding items from the space's bin while the Admins' bin keeps them, so nothing an author wrote is destroyed by one request. Must these operations destroy the data, or may aerie keep it out of reach of the compatible surface?

## What aerie does until answered

- A removed version leaves the surface: it is not listed, not readable with `version=`, and its number is never used again. Aerie's own history keeps it, and the removal is audited (`page.version.remove`).
- A purge and an emptied trash are aerie's own "empty bin": the items leave the space's trash and every read of the surface; the Admins' bin keeps them. A purge is audited (`page.purge`), an emptied trash as aerie's `bin.empty`.

See `src/Kb.Web/Actions/DataCenterContent.cs`.
