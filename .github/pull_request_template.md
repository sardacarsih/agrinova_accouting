## Summary

- What changed?
- Why was it needed?

## Scope

- Authentication / Access
- User management
- General Ledger
- Reports
- Inventory
- Database / migrations
- Repo / docs / CI

## Verification

List the checks you ran.

```powershell
dotnet build .\Accounting.csproj -nologo
dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj
```

## Database Impact

- [ ] No database changes
- [ ] SQL migration added
- [ ] Existing environment backfill required
- [ ] Documentation updated in `database/`

## Risks

Call out any behavioral regression risk, rollout concern, or permission change.

## Screenshots

Add UI screenshots here when relevant.

