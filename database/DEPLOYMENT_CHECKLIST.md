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
