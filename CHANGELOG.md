# Changelog

All notable changes to this repository will be documented in this file.

This project currently follows a simple keep-a-changelog style format.

## [Unreleased]

### Added

- GitHub CI workflow for Windows/.NET 8 build validation
- MIT license
- contributor guide, security policy, issue templates, PR template, and release checklist
- GitHub-oriented README sections for setup, release process, screenshots, and repository hygiene
- `appsettings.example.json` as the committed local configuration template
- `.gitignore` for WPF/.NET build output and local secrets

### Changed

- General Ledger navigation now requires explicit accounting `view` permissions
- Journal import/export actions now honor dedicated `transactions.import` and `transactions.export` permissions
- Report XLSX export now honors dedicated `accounting.reports.export` permission
- Integration coverage expanded for RBAC regression scenarios around GL navigation and export/import gating

## [2026-03-25]

### Added

- Initial public repository contents for the WPF accounting application
- PostgreSQL-backed authentication, RBAC, journal, reporting, and inventory codebase
- Database bootstrap, migration, and verification SQL scripts
- Integration test harness for database-backed workflows

### Changed

- Repository prepared for GitHub publication and ongoing collaboration

