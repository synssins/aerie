# Who sees a space made through the compatible surface

- **Building:** `POST /rest/api/space` (`createSpace`) and `POST /rest/api/space/_private` (`createPrivateSpace`).

## Question

`spaces.md` says `_private` makes "a global space only its creator can see", which implies a space made with `POST /rest/api/space` is seen by more people. It does not say by whom. Aerie spaces have a visibility: Private (only people and groups given a role), Signed in (everyone signed in reads it), or Anyone (also people not signed in). Which should `createSpace` use?

## What aerie does until answered

Both operations make a Private space, as aerie's own space creation does by default: only people and groups given a role in it, and the Admins, see it. An Admin can widen it in aerie's administration. See `src/Kb.Web/Actions/DataCenterSpaces.cs`.
