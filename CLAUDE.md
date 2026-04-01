# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WPF desktop accounting application (C# 12 / .NET 8) for agricultural estate management. Uses MVVM pattern with DevExpress 24.1 UI components and PostgreSQL (Npgsql, no ORM — direct SQL/prepared statements).

## Build & Run Commands

```bash
# Build
dotnet build ./Accounting.csproj -c Release --no-restore -nologo

# Run
dotnet run --project ./Accounting.csproj

# Integration tests (requires PostgreSQL)
dotnet run --project ./tools/IntegrationTests/IntegrationTests.csproj

# Override DB connection for tests
export AGRINOVA_PG_CONNECTION="Host=...;Port=5432;Database=...;Username=...;Password=..."
```

CI runs via `.github/workflows/ci.yml` — builds main app and integration tests on push/PR. No DB tests in CI.

## Architecture

**MVVM**: Views (XAML) → ViewModels (`ViewModelBase`, `RelayCommand<T>`) → Services → PostgreSQL.

**Startup flow**: `App.xaml` → `LoginWindow` → `AccessSelectionWindow` (role/company/location) → `MainWindow` shell.

### Key layers

- **`/Services/`** — All business logic and data access. The central service is `PostgresAccessControlService`, split across 35+ partial class files organized by domain (`.Journal.*`, `.Inventory.*`, `.Authorization`, `.CoreSchema`, `.Dashboard`, etc.).
- **`/ViewModels/`** — UI state and orchestration. `MainShellViewModel` is the shell; `UserManagementViewModel` is the largest (~131KB).
- **`/Views/`** — XAML pages/windows.
- **`/Infrastructure/`** — `AppServices` (static service locator), `IAppLogger`/`FileAppLogger`, `RelayCommand<T>`, `ViewModelBase`.
- **`/Resources/`** — Design tokens, styles, DevExpress theme bridges, component templates.
- **`/database/`** — PostgreSQL init scripts (`init_auth.sql`, `init_gl_accounts_master.sql`, `init_inventory.sql`) and migration scripts.

### RBAC model

Action-based permissions across 7 modules (accounting, inventory, master_data, admin, dashboard, reports, settings) with company/location scoping. Tables: `sec_roles`, `sec_modules`, `sec_submodules`, `sec_actions`, `sec_role_action_access`.

### Domain workflows

- **GL journals**: DRAFT → SUBMITTED → APPROVED → POSTED (multi-step approval)
- **Accounting periods**: Open/close with automated revenue/expense transfer
- **Inventory**: Stock in/out/transfer/opname, costing engines, central sync API
- **Reports**: Trial Balance, P&L, Balance Sheet, GL, Sub-Ledger, Cash Flow, Account Mutation — all export to XLSX

## Configuration

`appsettings.json` holds `DatabaseAuth` and `CentralSync` settings. Environment variables override config:
- `AGRINOVA_PG_CONNECTION` — DB connection string
- `AGRINOVA_AUTH_USERS_TABLE` — Users table name
- `AGRINOVA_AUTH_QUERY_TIMEOUT` — Query timeout seconds

## Build Dependencies

- DevExpress 24.1 packages sourced from local `.nuget/` cache (configured in `NuGet.Config`)
- `Directory.Build.props` disables NuGet audit warnings

## GL Account Format

Account codes follow the format `XX.XXXXX.XXX` (99.99999.999).
