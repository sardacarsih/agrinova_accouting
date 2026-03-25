# Security Policy

## Supported Versions

This repository currently treats the latest `main` branch as the supported version.

## Reporting a Vulnerability

Please do not open public GitHub issues for security vulnerabilities.

Instead, report security-sensitive findings privately to:

- `sarda.carsih@gmail.com`

Include:

- a short description of the issue
- affected area or file paths
- reproduction steps or proof of concept
- impact assessment if known
- any suggested remediation

## Sensitive Areas

Please report issues involving these areas with extra care:

- authentication and password verification
- RBAC and authorization bypasses
- PostgreSQL connection handling
- import/export flows that may expose protected data
- inventory central sync credentials or API key handling

## Repository Hygiene

- Do not commit real credentials to the repository.
- Use local `appsettings.json` or environment variables for secrets.
- Use [`appsettings.example.json`](D:/VSCODE/wpf/appsettings.example.json) as the committed template.

