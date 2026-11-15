# Clean-room policy

This repository is the **build room**. The code here is written from our own contracts, open standards and black-box test results, and from nothing else. Every contributor, person or AI agent, follows this policy on every change.

## The three repositories

| Repository | Role | What it may hold |
|---|---|---|
| `aerie-spec` | Spec room | Written behaviour specs and `openapi.yaml` contracts in our own words, a provenance ledger of every public reference read |
| `aerie` (this one) | Build room | The implementation, a pinned release of the contracts, our tests |
| `aerie-compat-lab` | Lab | Clients generated from vendors' published API specs, community client libraries, black-box test suites run against the aerie container image |

Spec writers and builders stay separated. Material reaches this repository only as a tagged contract release from `aerie-spec` (pinned in [contracts/](contracts/README.md)) or as a black-box result from `aerie-compat-lab`.

## Allowed in this repository

- Aerie's own code, tests, documentation and assets.
- Contract releases from `aerie-spec`, unedited, in `contracts/`.
- Open standards and RFCs (for example OAuth 2.0, OpenID Connect, HTTP, JSON Schema, OpenAPI).
- Black-box results from the lab: the requests sent, the responses aerie gave, and the lab's diff of them.
- Third-party packages under licenses compatible with Apache-2.0, recorded in the lock files.

## Never allowed in any repository

- Leaked or proprietary source code.
- Decompiled or disassembled binaries.
- Confidential information from a current or former employer.
- Copied UI assets, screenshots, icons or trademarks of another product.

## Not allowed in this repository (they belong in the spec room or the lab)

- Copies of vendor documentation or vendor API specification files.
- Clients or code generated from vendor API specification files.
- Response samples copied from vendor documentation.
- Customer code or customer data.

## Rules for contributors

1. **Contracts first.** A change to an API starts as a pull request to `aerie-spec`. Once released, the contract is pinned here, then the code changes, then the tests.
2. **Ask, don't look.** When a contract leaves a question open, open a spec question in `aerie-spec`. Do not look the answer up in vendor documentation from a build session.
3. **One role per session.** An AI agent session works in one repository, in one role. A session that has read vendor documentation or vendor spec files does not write implementation code here.
4. **Record provenance.** A new fixture, sample file, asset, font or outside reference gets a row in [PROVENANCE.md](PROVENANCE.md) in the same pull request.
5. **Review every merge.** Changes reach `main` only through a pull request with one approving human review and passing checks. The pull request template carries the clean-room checklist.

## If something slips through

Stop, and do not merge. Remove the material, record what happened and which commits it touched in [PROVENANCE.md](PROVENANCE.md) under "Incidents", and have the affected code rewritten by a contributor who has not seen the material.

## The existing code

Aerie's code up to commit `eea7e023` was written for aerie by its author with AI assistance, before this policy, as original work. It contains no vendor documentation, vendor specs or vendor code, and does not mention Confluence. This attestation is recorded in [PROVENANCE.md](PROVENANCE.md).
