-- Backfill script for deployed databases that already use action-based RBAC.
-- Adds dedicated inventory API actions for import/template workflows without
-- requiring a full auth bootstrap rerun.

BEGIN;

WITH api_inv_submodule AS (
    SELECT sm.id AS submodule_id
    FROM sec_submodules sm
    JOIN sec_modules mo ON mo.id = sm.module_id
    WHERE lower(mo.module_code) = 'inventory'
      AND lower(sm.submodule_code) = 'api_inv'
    LIMIT 1
),
action_seeds(action_code, action_name, sort_order, is_active) AS (
    VALUES
        ('download_import_template', 'Download Import Template', 55, TRUE),
        ('import_master_data', 'Import Master Data', 60, TRUE)
)
INSERT INTO sec_actions (submodule_id, action_code, action_name, sort_order, is_active)
SELECT s.submodule_id, a.action_code, a.action_name, a.sort_order, a.is_active
FROM api_inv_submodule s
CROSS JOIN action_seeds a
ON CONFLICT (submodule_id, action_code) DO UPDATE
SET action_name = EXCLUDED.action_name,
    sort_order = EXCLUDED.sort_order,
    is_active = EXCLUDED.is_active,
    updated_at = NOW();

WITH role_action_seeds(role_code, action_code) AS (
    VALUES
        ('INV_STAFF', 'download_import_template'),
        ('INV_STAFF', 'import_master_data')
)
INSERT INTO sec_role_action_access (role_id, action_id)
SELECT r.id, a.id
FROM role_action_seeds s
JOIN sec_roles r ON upper(r.code) = upper(s.role_code)
JOIN sec_submodules sm ON lower(sm.submodule_code) = 'api_inv'
JOIN sec_modules mo ON mo.id = sm.module_id AND lower(mo.module_code) = 'inventory'
JOIN sec_actions a ON a.submodule_id = sm.id AND lower(a.action_code) = lower(s.action_code)
ON CONFLICT DO NOTHING;

COMMIT;
