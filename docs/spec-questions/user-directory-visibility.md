# Who may read users and group memberships

- **Building:** the user operations (`contracts/behaviour/users.md`).
- **Operations:** `getUser`, `listUsers`, `getUserGroups`.

## Question

`users.md` says `listUsers` needs "an authenticated caller who may browse users", `getUserGroups` answers 403 "when the caller may not see group memberships", and `getUser` answers 403 to anonymous callers "when the site hides user profiles from anonymous users". It does not say who may browse users or see memberships, or whether deleted accounts and system accounts are listed.

1. Who may read another user, list users, and read another user's groups?
2. Does `listUsers` ("every account, enabled or not") include accounts that were deleted, and system accounts such as an emergency administrator?

## What aerie does until answered

- Anonymous callers: `getUser` 403 (as `users.md` says), `listUsers` and `getUserGroups` 401.
- `getUser` and `listUsers`: aerie's existing rule for looking people up applies: an Admin, an Owner of any space, or anyone who may edit pages somewhere. Anyone may read their own record.
- `getUserGroups`: an Admin, or the user asking about their own groups. Others get 403.
- Deleted accounts and the break-glass account are not listed and are not found (404). Deactivated accounts are listed, with `status: deactivated` when expanded.

See `src/Kb.Web/Actions/DataCenterUsers.cs`.
