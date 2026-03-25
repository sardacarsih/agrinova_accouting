# Accounting WPF

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-0C54C2)
![PostgreSQL](https://img.shields.io/badge/Database-PostgreSQL-336791)
![Platform](https://img.shields.io/badge/Platform-Windows-0078D4)
![License](https://img.shields.io/badge/License-MIT-green)

Desktop accounting application built with WPF on .NET 8 and PostgreSQL.

This project includes:
- username/password login against PostgreSQL
- role-based access control with company and location scoping
- General Ledger workflows for journal entry, posting, reporting, and period close
- inventory integration and dedicated inventory import RBAC actions
- service-level integration tests against a live PostgreSQL database

## Highlights

- Action-based RBAC with company and location scope
- Multi-step journal approval and posting workflow
- Financial report export to XLSX
- PostgreSQL-backed auth, access, and accounting services
- Integration-test harness for database-backed workflows

## Tech Stack

- .NET 8
- WPF
- PostgreSQL
- Npgsql

## Current Functional Areas

- Authentication and access-context selection
- User, role, company, and location administration
- General Ledger master data
- Journal workflow: `DRAFT -> SUBMITTED -> APPROVED -> POSTED`
- Financial reports:
  - Trial Balance
  - Profit and Loss
  - Balance Sheet
  - General Ledger
  - Sub Ledger
  - Cash Flow
  - Account Mutation
- Accounting period open/close workflow
- Inventory sync/import permission model

## Prerequisites

- Windows
- .NET 8 SDK
- PostgreSQL
- PowerShell

## Getting Started

### 1. Configure local settings

This repo ships with [`appsettings.example.json`](D:/VSCODE/wpf/appsettings.example.json). Create a local `appsettings.json` from it and fill in your database connection.

Expected local config shape:

```json
{
  "DatabaseAuth": {
    "ConnectionString": "Host=127.0.0.1;Port=5432;Database=your_database;Username=your_user;Password=your_password;Pooling=true;Timeout=8;Command Timeout=8;",
    "UsersTable": "public.app_users",
    "QueryTimeoutSeconds": 8
  },
  "CentralSync": {
    "BaseUrl": "",
    "ApiKey": "",
    "UploadPath": "/api/inventory/sync/upload",
    "DownloadPath": "/api/inventory/sync/download",
    "TimeoutSeconds": 30
  }
}
```

You can also override the database configuration with environment variables:

- `WFP_PG_CONNECTION`
- `WFP_AUTH_USERS_TABLE`
- `WFP_AUTH_QUERY_TIMEOUT`

PowerShell example:

```powershell
$env:WFP_PG_CONNECTION = "Host=127.0.0.1;Port=5432;Database=agrinova_accounting;Username=agrinova;Password=your_password;Pooling=true;Timeout=8;Command Timeout=8;"
$env:WFP_AUTH_USERS_TABLE = "public.app_users"
$env:WFP_AUTH_QUERY_TIMEOUT = "8"
```

### 2. Initialize the database

Run the core auth and RBAC schema first:

- [`database/init_auth.sql`](D:/VSCODE/wpf/database/init_auth.sql)

Then run the production-oriented GL account master migration:

- [`database/init_gl_accounts_master.sql`](D:/VSCODE/wpf/database/init_gl_accounts_master.sql)

Available database scripts:

- `init_auth.sql`
- `init_gl_accounts_master.sql`
- `init_inventory.sql`
- `gl_accounts_regression_checks.sql`
- `gl_accounts_cleanup_candidates.sql`
- `backfill_inventory_api_inv_import_actions.sql`
- `verify_inventory_api_inv_import_actions.sql`
- `lock_gl_inventory_role_policy.sql`
- `migrate_module_to_scope.sql`

### 3. Default login

Seed credentials from the auth script:

- Username: `admin`
- Password: `Admin@123`

### 4. Run the app

```powershell
dotnet run --project .\Accounting.csproj
```

## RBAC Notes

The app uses action-based RBAC. Recent accounting and inventory changes split read/import/export permissions into dedicated actions.

Examples:

- `accounting.reports.view`
- `accounting.reports.export`
- `accounting.transactions.import`
- `accounting.transactions.export`
- `inventory.api_inv.download_import_template`
- `inventory.api_inv.import_master_data`

Important behavior:

- General Ledger navigation is exposed from explicit `view` permissions.
- Journal import and journal export require their dedicated transaction actions.
- Report XLSX export requires `accounting.reports.export`.

For existing environments that were initialized before the inventory RBAC split, run:

- [`database/backfill_inventory_api_inv_import_actions.sql`](D:/VSCODE/wpf/database/backfill_inventory_api_inv_import_actions.sql)
- [`database/verify_inventory_api_inv_import_actions.sql`](D:/VSCODE/wpf/database/verify_inventory_api_inv_import_actions.sql)

Deployment notes are in [`database/DEPLOYMENT_CHECKLIST.md`](D:/VSCODE/wpf/database/DEPLOYMENT_CHECKLIST.md).

## Password Hash Format

Expected format:

`pbkdf2-sha256$<iterations>$<base64_salt>$<base64_hash>`

Generate a hash with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-WfpPasswordHash.ps1 -Password "Password123!"
```

## Reporting and Period Close

Reports are sourced from `gl_ledger_entries`, which are generated when journals are posted.

Period close currently includes:

- rejection when the target month still has journals not yet `POSTED`
- automatic creation of a closing journal
- transfer of `REVENUE` and `EXPENSE` balances into retained earnings
- accounting-equation validation during post/close flow

## Integration Tests

The integration tests run against a real PostgreSQL database.

Run them with:

```powershell
dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj
```

If needed, set the database connection first:

```powershell
$env:WFP_PG_CONNECTION = "Host=127.0.0.1;Port=5432;Database=agrinova_accounting;Username=agrinova;Password=your_password;Pooling=true;Timeout=8;Command Timeout=8;"
dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj
```

## Project Layout

```text
Controls/        Reusable WPF controls
Converters/      WPF value converters
Infrastructure/  Commands, logging, and shared plumbing
Resources/       Application resources and styling assets
Services/        PostgreSQL access, RBAC, journals, reports, inventory, and workflows
ViewModels/      UI state and workflow orchestration
Views/           WPF views and workspace components
database/        SQL bootstrap, migrations, and verification scripts
scripts/         Utility scripts such as password hash generation
tools/           Integration test harness and support tools
```

## Roadmap

- Complete remaining placeholder accounting menus such as custom reports and budgeting workflows
- Expand automated coverage for more RBAC combinations and UI-level regressions
- Improve deployment/bootstrap guidance for fresh and existing environments
- Add release packaging guidance for non-developer Windows deployments

## Known Limitations

- Integration tests require a live PostgreSQL database and are not isolated with local containers in this repo
- Some accounting menus are present as structural placeholders and are not fully implemented yet
- Local runtime configuration is file-based and expects per-machine setup through `appsettings.json` or environment variables
- This repo currently does not include CI configuration or release automation

## Contributing

1. Create a feature branch for your change.
2. Keep secrets out of source control and use `appsettings.example.json` as the committed template.
3. Run a local build before opening a PR:

```powershell
dotnet build .\Accounting.csproj -nologo
```

4. Run integration tests when your change touches database, RBAC, journal, inventory, or reporting behavior:

```powershell
dotnet run --project .\tools\IntegrationTests\IntegrationTests.csproj
```

5. Document any required SQL migration or backfill in the `database/` folder and mention it in the PR summary.

Full contributor guidance is available in [`CONTRIBUTING.md`](D:/VSCODE/wpf/CONTRIBUTING.md).

## License

This repository is licensed under the MIT License. See [`LICENSE`](D:/VSCODE/wpf/LICENSE).

## Publish Safety

This repo ignores local secrets and generated outputs via [`.gitignore`](D:/VSCODE/wpf/.gitignore).

Notably ignored:

- `appsettings.json`
- `.vs/`
- `bin/`
- `obj/`
- `artifacts/`

Use `appsettings.example.json` as the committed template for new environments.
