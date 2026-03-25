-- Legacy bridge script:
-- migrate role permission mapping from scope-based tables
-- (sec_access_scopes/sec_role_scope_access) into action-based tables
-- (sec_modules/sec_submodules/sec_actions/sec_role_action_access).

BEGIN;

INSERT INTO sec_role_action_access (role_id, action_id)
SELECT DISTINCT rsa.role_id, a.id
FROM sec_role_scope_access rsa
JOIN sec_access_scopes s ON s.id = rsa.scope_id
JOIN sec_actions a ON a.is_active = TRUE
JOIN sec_submodules sm ON sm.id = a.submodule_id AND sm.is_active = TRUE
JOIN sec_modules mo ON mo.id = sm.module_id AND mo.is_active = TRUE
WHERE
    (lower(s.code) = 'dashboard'
        AND lower(a.action_code) = 'view'
        AND (
            (lower(mo.module_code) = 'accounting' AND lower(sm.submodule_code) = 'dashboard')
            OR (lower(mo.module_code) = 'inventory' AND lower(sm.submodule_code) = 'dashboard')
        ))
    OR (lower(s.code) = 'master_data'
        AND lower(mo.module_code) = 'accounting'
        AND lower(sm.submodule_code) = 'master_data')
    OR (lower(s.code) = 'inventory'
        AND lower(mo.module_code) = 'inventory')
    OR (lower(s.code) = 'transactions'
        AND lower(mo.module_code) = 'accounting'
        AND lower(sm.submodule_code) = 'transactions')
    OR (lower(s.code) = 'approve'
        AND lower(a.action_code) IN ('approve', 'post'))
    OR (lower(s.code) = 'reports'
        AND (
            (lower(mo.module_code) = 'accounting' AND lower(sm.submodule_code) = 'reports')
            OR (lower(mo.module_code) = 'inventory' AND lower(sm.submodule_code) = 'reports')
        ))
    OR (lower(s.code) = 'settings'
        AND (
            (lower(mo.module_code) = 'accounting' AND lower(sm.submodule_code) = 'settings')
            OR (lower(mo.module_code) = 'inventory' AND lower(sm.submodule_code) = 'api_inv')
        ))
    OR (lower(s.code) = 'user_management'
        AND lower(mo.module_code) = 'accounting'
        AND lower(sm.submodule_code) = 'user_management')
ON CONFLICT DO NOTHING;

COMMIT;
