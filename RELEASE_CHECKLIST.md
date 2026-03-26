# Release Checklist

Use this checklist before publishing a tagged release or deployment build.

## Code and Validation

- [ ] `main` is up to date
- [ ] `dotnet build .\Accounting.csproj -nologo` passes
- [ ] `dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj` passes for the target database
- [ ] Any required manual smoke tests are completed

## Database and Migration Review

- [ ] Required SQL scripts are present in `database/`
- [ ] Existing-environment backfills are documented
- [ ] Verification scripts exist for RBAC or schema changes where needed
- [ ] Deployment notes are updated

## Security and Configuration

- [ ] No real credentials are tracked in Git
- [ ] `appsettings.json` remains untracked
- [ ] `appsettings.example.json` reflects the current config shape
- [ ] Any environment-variable requirements are documented
- [ ] Deployment environments use `AGRINOVA_PG_CONNECTION`, `AGRINOVA_AUTH_USERS_TABLE`, `AGRINOVA_AUTH_QUERY_TIMEOUT`, and `AGRINOVA_ENVIRONMENT` where applicable

## Documentation

- [ ] `README.md` reflects current setup and feature status
- [ ] `CONTRIBUTING.md` is still accurate
- [ ] `SECURITY.md` contact path is still valid
- [ ] Release notes or changelog summary is prepared

## GitHub / Release

- [ ] CI is green
- [ ] Release tag/version is chosen
- [ ] Screenshots or release artifacts are attached if needed
- [ ] Any breaking-change or migration note is called out clearly
- [ ] Release notes explicitly mention the removal of legacy `WFP_*` environment variables
