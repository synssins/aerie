# Build numbers in server information

- **Building:** `GET /rest/api/server-information` (operation `getServerInformation`).

## Question

`common.md` says `buildNumber` and `marketplaceBuildNumber` must be "numbers consistent with that version" and that aerie keeps a table mapping each presented version to its build number, and that `buildDate` is an ISO 8601 date-time. The contract does not give the table, the build number for the default presented version `9.2.4`, or what `buildDate` should be. What numbers and date should aerie report for each version it may present?

## What aerie does until answered

The version, both build numbers and the build date are settings (`Kb:DataCenter:Version`, `Kb:DataCenter:BuildNumber`, `Kb:DataCenter:MarketplaceBuildNumber`, `Kb:DataCenter:BuildDate`). When a build number is not set, aerie derives one from the version as major × 1,000,000 + minor × 1,000 + patch (`9.2.4` gives `9002004`). The default build date is `2026-01-01T00:00:00.000Z`. See `src/Kb.Web/Routes/DataCenterRoutes.cs`.
