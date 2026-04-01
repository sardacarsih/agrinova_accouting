# Deployment Checklist

## Inventory Import RBAC Backfill

Use this checklist when deploying the dedicated inventory import/template permissions to an existing database.

### Scope
- Existing environments that already ran older `init_auth.sql`
- Environments where role assignment for inventory import/template will be managed after deployment

### Checklist
1. Confirm the application build contains the new `inventory.api_inv` actions:
   - `download_import_template`
   - `import_master_data`
2. Run the idempotent backfill script:
   - [backfill_inventory_api_inv_import_actions.sql](D:\VSCODE\wpf\database\backfill_inventory_api_inv_import_actions.sql)
3. Run the read-only verification script:
   - [verify_inventory_api_inv_import_actions.sql](D:\VSCODE\wpf\database\verify_inventory_api_inv_import_actions.sql)
4. Verify the new actions exist in `sec_actions`.
5. Verify `INV_STAFF` received default access to both actions if that role exists.
6. Confirm the application no longer shows the startup RBAC warning for missing inventory import actions.
7. Open `User Management > Roles` and confirm these labels appear under `Inventory > API Inv`:
   - `Download Import Template`
   - `Import Master Data`
8. Re-check intended roles:
   - roles that should import inventory data have `Import Master Data`
   - roles that should only download the template have `Download Import Template`
9. Smoke test with a non-super user:
   - confirm direct category/item CRUD still follows CRUD permissions
   - confirm import works only with the dedicated import permission
   - confirm template download works only with the dedicated template permission

### Notes
- Fresh environments that already run the latest [init_auth.sql](D:\VSCODE\wpf\database\init_auth.sql) do not need the backfill script.
- The script is safe to run multiple times.
- Do not assume `kategori.update` or `item.update` is enough anymore for import/template workflows.

## GL Account Prefix Compatibility

Use this checklist when upgrading an existing accounting database that still enforces the old numeric-only account prefix rule.

### Scope
- Existing environments that previously accepted only `99.99999.999`
- Environments that now use company/scope prefixes such as `HO.33000.001` or `KB.51000.001`

### Checklist
1. Confirm the application build includes the account-code validation update and retained-earnings fix.
2. Run the idempotent compatibility script:
   - [allow_alphanumeric_account_prefix.sql](D:\VSCODE\wpf\database\allow_alphanumeric_account_prefix.sql)
3. Verify new or existing GL accounts with a 2-character alphanumeric prefix can be inserted or updated.
4. Smoke test account master save/import for:
   - a summary account
   - a posting child account
   - a retained earnings close-period flow
5. Confirm journal draft validation still rejects non-posting cost center selections and accepts posting cost center selections.

### Notes
- Fresh environments that run the latest [init_gl_accounts_master.sql](D:\VSCODE\wpf\database\init_gl_accounts_master.sql) already get the updated `XX.99999.999` rule.
- The compatibility script is safe to run multiple times.

## Workbook COA Reseed

Use this checklist when reseeding the active COA structure for `company_id = 1` from the workbook source in this repo.

### Checklist
1. Confirm the application build no longer depends on the legacy runtime `10.*` sample seed path.
2. Run the workbook-driven reseed runner:
   - [reseed_gl_accounts_company1_from_workbook.ps1](D:\VSCODE\wpf\scripts\reseed_gl_accounts_company1_from_workbook.ps1)
3. Run the read-only verification script if it was not executed by the runner:
   - [verify_gl_accounts_workbook_company1.sql](D:\VSCODE\wpf\database\verify_gl_accounts_workbook_company1.sql)
4. Verify the expected `20`, `80`, and `81` nodes exist with the correct hierarchy level and posting flags.
5. Smoke test account master save/import with a multi-level non-posting parent and a posting leaf account.
