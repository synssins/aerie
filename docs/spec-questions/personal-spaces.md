# Who may create a personal space

- **Building:** `POST /rest/api/space/personal` (`createPersonalSpace`).

## Question

`spaces.md` says the operation creates the caller's personal space and answers `403` "when the caller may not have one", without saying who may. Aerie's own rule is that only an Admin creates spaces (Owners and others cannot). Should any signed-in user be able to create their own personal space, and what visibility should `isPrivate: false` give it?

## What aerie does until answered

Only an Admin may create a personal space (anyone else gets `403`), keeping aerie's rule. The space is Private whatever `isPrivate` says, its key is `~` followed by the owner's user name in lower case, and its owner gets the Owner role. A second one gives `409`. See `src/Kb.Web/Actions/DataCenterSpaces.cs`.
