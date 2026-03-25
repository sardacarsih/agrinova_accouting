-- Read-only verification script for dedicated inventory import/template permissions.
-- Safe to run on deployed databases before or after the backfill script.

WITH required_actions(action_code) AS (
    VALUES
        ('download_import_template'),
        ('import_master_data')
),
action_status AS (
    SELECT
        ra.action_code,
        EXISTS (
            SELECT 1
            FROM sec_actions a
            JOIN sec_submodules sm ON sm.id = a.submodule_id
            JOIN sec_modules mo ON mo.id = sm.module_id
            WHERE lower(mo.module_code) = 'inventory'
              AND lower(sm.submodule_code) = 'api_inv'
              AND lower(a.action_code) = lower(ra.action_code)
              AND a.is_active = TRUE
        ) AS action_exists
    FROM required_actions ra
),
inv_staff_role AS (
    SELECT id
    FROM sec_roles
    WHERE lower(role_code) = 'inv_staff'
    LIMIT 1
),
grant_status AS (
    SELECT
        ra.action_code,
        EXISTS (
            SELECT 1
            FROM inv_staff_role r
            JOIN sec_role_action_access raa ON raa.role_id = r.id
            JOIN sec_actions a ON a.id = raa.action_id
            JOIN sec_submodules sm ON sm.id = a.submodule_id
            JOIN sec_modules mo ON mo.id = sm.module_id
            WHERE lower(mo.module_code) = 'inventory'
              AND lower(sm.submodule_code) = 'api_inv'
              AND lower(a.action_code) = lower(ra.action_code)
              AND a.is_active = TRUE
        ) AS inv_staff_has_access
    FROM required_actions ra
)
SELECT
    'ACTION_STATUS' AS check_name,
    action_code AS target,
    CASE WHEN action_exists THEN 'OK' ELSE 'MISSING' END AS status
FROM action_status
ORDER BY action_code;

WITH required_actions(action_code) AS (
    VALUES
        ('download_import_template'),
        ('import_master_data')
),
inv_staff_role AS (
    SELECT id
    FROM sec_roles
    WHERE lower(role_code) = 'inv_staff'
    LIMIT 1
),
grant_status AS (
    SELECT
        ra.action_code,
        EXISTS (
            SELECT 1
            FROM inv_staff_role r
            JOIN sec_role_action_access raa ON raa.role_id = r.id
            JOIN sec_actions a ON a.id = raa.action_id
            JOIN sec_submodules sm ON sm.id = a.submodule_id
            JOIN sec_modules mo ON mo.id = sm.module_id
            WHERE lower(mo.module_code) = 'inventory'
              AND lower(sm.submodule_code) = 'api_inv'
              AND lower(a.action_code) = lower(ra.action_code)
              AND a.is_active = TRUE
        ) AS inv_staff_has_access
    FROM required_actions ra
)
SELECT
    'INV_STAFF_GRANT' AS check_name,
    action_code AS target,
    CASE
        WHEN EXISTS (SELECT 1 FROM inv_staff_role) THEN CASE WHEN inv_staff_has_access THEN 'OK' ELSE 'MISSING' END
        ELSE 'ROLE_NOT_FOUND'
    END AS status
FROM grant_status
ORDER BY action_code;

WITH required_actions(action_code) AS (
    VALUES
        ('download_import_template'),
        ('import_master_data')
),
action_status AS (
    SELECT
        ra.action_code,
        EXISTS (
            SELECT 1
            FROM sec_actions a
            JOIN sec_submodules sm ON sm.id = a.submodule_id
            JOIN sec_modules mo ON mo.id = sm.module_id
            WHERE lower(mo.module_code) = 'inventory'
              AND lower(sm.submodule_code) = 'api_inv'
              AND lower(a.action_code) = lower(ra.action_code)
              AND a.is_active = TRUE
        ) AS action_exists
    FROM required_actions ra
)
SELECT
    'SUMMARY' AS check_name,
    CASE
        WHEN COUNT(*) FILTER (WHERE action_exists) = COUNT(*) THEN 'OK'
        ELSE 'MISSING_ACTIONS'
    END AS status,
    string_agg(action_code, ', ' ORDER BY action_code) FILTER (WHERE NOT action_exists) AS missing_actions
FROM action_status;
