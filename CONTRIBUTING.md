# Contributing

## Development Flow

1. Create a feature branch from `main`.
2. Keep changes focused and document any required SQL migration in [`database/`](D:/VSCODE/wpf/database).
3. Do not commit local secrets or machine-specific configuration.
4. When changing WPF UI, follow the internal guide in [`docs/ui-theme-and-skin-guidelines.md`](D:/VSCODE/wpf/docs/ui-theme-and-skin-guidelines.md) so text, colors, and states stay aligned with curated skins.
5. Open a pull request with a short summary, verification steps, and any migration/backfill notes.

## Local Setup

- Use [`appsettings.example.json`](D:/VSCODE/wpf/appsettings.example.json) as the template for your local `appsettings.json`.
- Prefer environment variables for temporary or sensitive database overrides:
  - `AGRINOVA_PG_CONNECTION`
  - `AGRINOVA_AUTH_USERS_TABLE`
  - `AGRINOVA_AUTH_QUERY_TIMEOUT`

## Required Checks

Run a local build before opening a PR:

```powershell
dotnet build .\Accounting.csproj -nologo
```

If your change affects database access, RBAC, journals, reports, inventory, or workflow behavior, run the integration tests too:

```powershell
dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj
```

## Database Changes

- Put schema/bootstrap/backfill scripts in [`database/`](D:/VSCODE/wpf/database).
- Make scripts idempotent when they are intended for existing environments.
- Add or update verification scripts when introducing permission or migration changes.
- Mention rollout expectations clearly in the PR description.

## Repository Rules

- `appsettings.json` is local-only and must not be committed.
- Build output folders such as `bin/`, `obj/`, `.vs/`, and `artifacts/` stay untracked.
- Prefer small, reviewable commits with a clear message.
