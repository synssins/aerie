# HTTP Basic and accounts that use one-time codes

- **Building:** HTTP Basic sign-in on the Data Center compatible surface (`contracts/behaviour/common.md`, Authentication).
- **Operations:** every operation in `contracts/datacenter/openapi.yaml`.

## Question

`common.md` says Basic (`username:password`) is accepted on every operation. In aerie, some accounts must give a one-time code after the password at sign-in: every Admin, anyone who turned codes on, and everyone when the site setting says so. The break-glass account signs in with a password only, and each of its sign-ins alerts the Admins. A password alone over Basic would skip the code (or, for break-glass, raise an alert on every request).

1. Should Basic accept the password of an account that must use codes?
2. Should Basic accept an API token (or another secret) in place of the password, so such accounts can still use Basic clients?
3. Should the break-glass account be able to use Basic at all?

## What aerie does until answered

The most conservative reading: Basic is refused (401) for every account that must use codes and for the break-glass account, with the refusal audited. Those accounts can still call the surface with a signed-in session or an API token (`Authorization: Bearer`). Basic is read only under `/rest/api`; aerie's own API ignores it, as before. See `src/Kb.Web/DataCenter/BasicAuth.cs`.
