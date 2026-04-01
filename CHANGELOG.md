# Changelog

All notable changes to this repository will be documented in this file.

This project currently follows a simple keep-a-changelog style format.

## [Unreleased]

## [2026-04-01]

### Added

- GitHub CI workflow for Windows/.NET 8 build validation
- MIT license
- contributor guide, security policy, issue templates, PR template, and release checklist
- GitHub-oriented README sections for setup, release process, screenshots, and repository hygiene
- `appsettings.example.json` as the committed local configuration template
- `.gitignore` for WPF/.NET build output and local secrets
- repo-local `NuGet.Config` for offline/local package restore during build and validation
- `database/allow_alphanumeric_account_prefix.sql` for upgrading existing GL schemas to the `99.99999.999` account format
- Estate hierarchy (Estate > Division > Block) master data management with three-level organizational structure
- XLSX import/export for estate hierarchy and GL accounts with row-level validation and error reporting
- Block and subledger selection picker dialogs for journal line posting
- Master data workspace with tabbed views for accounts, cost centers, periods, and estate hierarchy
- Database scripts for GL account reseeding and UAT user assignment
- RBAC actions for master data operations: `master_data.create`, `master_data.update`, `master_data.delete`, `master_data.import_master_data`

### Changed

- General Ledger navigation now requires explicit accounting `view` permissions
- Journal import/export actions now honor dedicated `transactions.import` and `transactions.export` permissions
- Report XLSX export now honors dedicated `accounting.reports.export` permission
- Integration coverage expanded for RBAC regression scenarios around GL navigation and export/import gating
- Application branding renamed from `WFP Suite` to `AgrInova Suite`
- Runtime environment variables renamed from `WFP_*` to `AGRINOVA_*` with no backward-compatibility fallback
- GL account validation now accepts 2-character alphanumeric prefixes such as `HO` and `KB`
- Period close retained-earnings posting now resolves the company-prefixed retained earnings account consistently
- Journal draft validation now distinguishes missing, non-posting, and posting cost center selections from `gl_cost_centers`

## [2026-03-25]

### Added

- Initial public repository contents for the WPF accounting application
- PostgreSQL-backed authentication, RBAC, journal, reporting, and inventory codebase
- Database bootstrap, migration, and verification SQL scripts
- Integration test harness for database-backed workflows

### Changed

- Repository prepared for GitHub publication and ongoing collaboration
