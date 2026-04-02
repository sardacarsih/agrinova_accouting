CREATE TABLE IF NOT EXISTS app_users (
    id BIGSERIAL PRIMARY KEY,
    username VARCHAR(100) NOT NULL UNIQUE,
    full_name VARCHAR(160),
    email VARCHAR(255),
    password_hash TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE app_users ADD COLUMN IF NOT EXISTS full_name VARCHAR(160);
ALTER TABLE app_users ADD COLUMN IF NOT EXISTS email VARCHAR(255);
ALTER TABLE app_users ADD COLUMN IF NOT EXISTS default_company_id BIGINT;
ALTER TABLE app_users ADD COLUMN IF NOT EXISTS default_location_id BIGINT;

CREATE TABLE IF NOT EXISTS sec_roles (
    id BIGSERIAL PRIMARY KEY,
    code VARCHAR(80) NOT NULL UNIQUE,
    name VARCHAR(160) NOT NULL,
    is_super_role BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sec_modules (
    id BIGSERIAL PRIMARY KEY,
    module_code VARCHAR(80) NOT NULL,
    module_name VARCHAR(160) NOT NULL,
    sort_order INT NOT NULL DEFAULT 100,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sec_submodules (
    id BIGSERIAL PRIMARY KEY,
    module_id BIGINT NOT NULL REFERENCES sec_modules(id) ON DELETE CASCADE,
    submodule_code VARCHAR(80) NOT NULL,
    submodule_name VARCHAR(160) NOT NULL,
    sort_order INT NOT NULL DEFAULT 100,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sec_actions (
    id BIGSERIAL PRIMARY KEY,
    submodule_id BIGINT NOT NULL REFERENCES sec_submodules(id) ON DELETE CASCADE,
    action_code VARCHAR(80) NOT NULL,
    action_name VARCHAR(160) NOT NULL,
    sort_order INT NOT NULL DEFAULT 100,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS org_companies (
    id BIGSERIAL PRIMARY KEY,
    code VARCHAR(80) NOT NULL UNIQUE,
    name VARCHAR(200) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS org_locations (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    code VARCHAR(80) NOT NULL,
    name VARCHAR(200) NOT NULL,
    location_type VARCHAR(20) NOT NULL DEFAULT 'OFFICE',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_org_locations_type CHECK (location_type IN ('ESTATE','MILL','OFFICE')),
    CONSTRAINT uq_location_company_code UNIQUE (company_id, code)
);

CREATE TABLE IF NOT EXISTS sec_user_roles (
    user_id BIGINT NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
    role_id BIGINT NOT NULL REFERENCES sec_roles(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_id)
);

WITH ranked_user_roles AS (
    SELECT user_id,
           role_id,
           ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY role_id) AS rn
    FROM sec_user_roles
)
DELETE FROM sec_user_roles ur
USING ranked_user_roles r
WHERE ur.user_id = r.user_id
  AND ur.role_id = r.role_id
  AND r.rn > 1;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_sec_user_roles_user_id'
    ) THEN
        ALTER TABLE sec_user_roles
            ADD CONSTRAINT uq_sec_user_roles_user_id UNIQUE (user_id);
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS sec_role_action_access (
    role_id BIGINT NOT NULL REFERENCES sec_roles(id) ON DELETE CASCADE,
    action_id BIGINT NOT NULL REFERENCES sec_actions(id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, action_id)
);

DROP TABLE IF EXISTS sec_role_location_access;
DROP TABLE IF EXISTS sec_role_company_access;

CREATE TABLE IF NOT EXISTS sec_user_company_access (
    user_id BIGINT NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, company_id)
);

CREATE TABLE IF NOT EXISTS sec_user_location_access (
    user_id BIGINT NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, location_id)
);

DELETE FROM sec_user_location_access ula
USING org_locations l
WHERE l.id = ula.location_id
  AND NOT EXISTS (
      SELECT 1
      FROM sec_user_company_access uca
      WHERE uca.user_id = ula.user_id
        AND uca.company_id = l.company_id
  );

CREATE OR REPLACE FUNCTION fn_validate_sec_user_location_access()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM org_locations l
        JOIN sec_user_company_access uca
          ON uca.user_id = NEW.user_id
         AND uca.company_id = l.company_id
        WHERE l.id = NEW.location_id
    ) THEN
        RAISE EXCEPTION 'User location assignment must match assigned company.';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_validate_sec_user_location_access ON sec_user_location_access;
CREATE TRIGGER trg_validate_sec_user_location_access
BEFORE INSERT OR UPDATE ON sec_user_location_access
FOR EACH ROW
EXECUTE FUNCTION fn_validate_sec_user_location_access();

CREATE OR REPLACE FUNCTION fn_validate_app_user_default_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_is_super_role BOOLEAN := FALSE;
BEGIN
    IF NEW.default_company_id IS NULL AND NEW.default_location_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF NEW.default_company_id IS NULL OR NEW.default_location_id IS NULL THEN
        RAISE EXCEPTION 'Default company and default location must be set together.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM org_locations l
        WHERE l.id = NEW.default_location_id
          AND l.company_id = NEW.default_company_id
    ) THEN
        RAISE EXCEPTION 'Default location must belong to default company.';
    END IF;

    SELECT EXISTS (
        SELECT 1
        FROM sec_user_roles ur
        JOIN sec_roles r ON r.id = ur.role_id
        WHERE ur.user_id = NEW.id
          AND COALESCE(r.is_super_role, FALSE) = TRUE
    )
    INTO v_is_super_role;

    IF v_is_super_role THEN
        RETURN NEW;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM sec_user_company_access uca
        WHERE uca.user_id = NEW.id
          AND uca.company_id = NEW.default_company_id
    ) THEN
        RAISE EXCEPTION 'Default company must be assigned to user.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM sec_user_location_access ula
        WHERE ula.user_id = NEW.id
          AND ula.location_id = NEW.default_location_id
    ) THEN
        RAISE EXCEPTION 'Default location must be assigned to user.';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_validate_app_user_default_scope ON app_users;
CREATE TRIGGER trg_validate_app_user_default_scope
BEFORE INSERT OR UPDATE ON app_users
FOR EACH ROW
EXECUTE FUNCTION fn_validate_app_user_default_scope();

CREATE TABLE IF NOT EXISTS sec_audit_logs (
    id BIGSERIAL PRIMARY KEY,
    entity_type VARCHAR(40) NOT NULL,
    entity_id BIGINT NOT NULL,
    action VARCHAR(60) NOT NULL,
    actor_username VARCHAR(100) NOT NULL,
    details TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS fa_asset_registers (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    asset_no VARCHAR(80) NOT NULL,
    asset_name VARCHAR(200) NOT NULL,
    asset_category VARCHAR(80) NOT NULL DEFAULT '',
    acquisition_date DATE NOT NULL DEFAULT CURRENT_DATE,
    acquisition_cost NUMERIC(18,2) NOT NULL DEFAULT 0,
    useful_life_months INT NOT NULL DEFAULT 0,
    residual_value NUMERIC(18,2) NOT NULL DEFAULT 0,
    status VARCHAR(20) NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','DISPOSED','RETIRED')),
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_fa_asset_registers_company_location_no UNIQUE (company_id, location_id, asset_no)
);

CREATE TABLE IF NOT EXISTS fa_asset_depreciations (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    asset_register_id BIGINT NOT NULL REFERENCES fa_asset_registers(id) ON DELETE CASCADE,
    period_month DATE NOT NULL,
    depreciation_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
    accumulated_depreciation NUMERIC(18,2) NOT NULL DEFAULT 0,
    book_value NUMERIC(18,2) NOT NULL DEFAULT 0,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_fa_asset_depreciations_period UNIQUE (asset_register_id, period_month)
);

ALTER TABLE org_locations ADD COLUMN IF NOT EXISTS location_type VARCHAR(20) NOT NULL DEFAULT 'OFFICE';
UPDATE org_locations
SET location_type = 'OFFICE'
WHERE location_type IS NULL OR btrim(location_type) = '';
ALTER TABLE org_locations DROP CONSTRAINT IF EXISTS chk_org_locations_type;
ALTER TABLE org_locations
    ADD CONSTRAINT chk_org_locations_type CHECK (location_type IN ('ESTATE','MILL','OFFICE'));

CREATE UNIQUE INDEX IF NOT EXISTS ux_sec_modules_module_code
    ON sec_modules (module_code);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sec_submodules_module_submodule
    ON sec_submodules (module_id, submodule_code);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sec_actions_submodule_action
    ON sec_actions (submodule_id, action_code);
CREATE INDEX IF NOT EXISTS ix_sec_submodules_module_id
    ON sec_submodules (module_id);
CREATE INDEX IF NOT EXISTS ix_sec_actions_submodule_id
    ON sec_actions (submodule_id);
CREATE INDEX IF NOT EXISTS ix_sec_user_roles_role_id
    ON sec_user_roles (role_id);
CREATE INDEX IF NOT EXISTS ix_sec_role_action_access_action_id
    ON sec_role_action_access (action_id);
CREATE INDEX IF NOT EXISTS ix_sec_user_company_access_company_id
    ON sec_user_company_access (company_id);
CREATE INDEX IF NOT EXISTS ix_sec_user_location_access_location_id
    ON sec_user_location_access (location_id);

CREATE OR REPLACE VIEW vw_user_effective_permissions AS
SELECT ur.user_id,
       mo.module_code,
       sm.submodule_code,
       a.action_code
FROM sec_user_roles ur
JOIN sec_roles r ON r.id = ur.role_id
JOIN sec_role_action_access raa ON raa.role_id = ur.role_id
JOIN sec_actions a ON a.id = raa.action_id
JOIN sec_submodules sm ON sm.id = a.submodule_id
JOIN sec_modules mo ON mo.id = sm.module_id
WHERE r.is_active = TRUE
  AND mo.is_active = TRUE
  AND sm.is_active = TRUE
  AND a.is_active = TRUE;

CREATE OR REPLACE FUNCTION fn_user_has_permission(
    p_username TEXT,
    p_module TEXT,
    p_submodule TEXT,
    p_action TEXT,
    p_company_id BIGINT DEFAULT NULL,
    p_location_id BIGINT DEFAULT NULL)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
    v_user_id BIGINT;
    v_has_super_role BOOLEAN := FALSE;
BEGIN
    IF p_username IS NULL OR btrim(p_username) = '' OR
       p_module IS NULL OR btrim(p_module) = '' OR
       p_submodule IS NULL OR btrim(p_submodule) = '' OR
       p_action IS NULL OR btrim(p_action) = '' THEN
        RETURN FALSE;
    END IF;

    SELECT u.id,
           EXISTS (
               SELECT 1
               FROM sec_user_roles ur
               JOIN sec_roles r ON r.id = ur.role_id
               WHERE ur.user_id = u.id
                 AND r.is_active = TRUE
                 AND COALESCE(r.is_super_role, FALSE) = TRUE
           )
    INTO v_user_id, v_has_super_role
    FROM app_users u
    WHERE lower(u.username) = lower(btrim(p_username))
      AND u.is_active = TRUE
    LIMIT 1;

    IF v_user_id IS NULL THEN
        RETURN FALSE;
    END IF;

    IF v_has_super_role THEN
        RETURN TRUE;
    END IF;

    IF p_company_id IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM sec_user_company_access uca
        JOIN org_companies c ON c.id = uca.company_id
        WHERE uca.user_id = v_user_id
          AND uca.company_id = p_company_id
          AND c.is_active = TRUE
    ) THEN
        RETURN FALSE;
    END IF;

    IF p_location_id IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM sec_user_location_access ula
        JOIN org_locations l ON l.id = ula.location_id
        WHERE ula.user_id = v_user_id
          AND ula.location_id = p_location_id
          AND l.is_active = TRUE
          AND (p_company_id IS NULL OR l.company_id = p_company_id)
    ) THEN
        RETURN FALSE;
    END IF;

    RETURN EXISTS (
        SELECT 1
        FROM vw_user_effective_permissions p
        WHERE p.user_id = v_user_id
          AND lower(p.module_code) = lower(btrim(p_module))
          AND lower(p.submodule_code) = lower(btrim(p_submodule))
          AND lower(p.action_code) = lower(btrim(p_action))
    );
END
$$;

-- Password hash format expected by app:
-- pbkdf2-sha256$<iterations>$<base64_salt>$<base64_hash>
-- You can generate hash via scripts/New-AgrInovaPasswordHash.ps1
INSERT INTO app_users (username, full_name, email, password_hash, is_active)
VALUES (
    'admin',
    'System Administrator',
    'admin@agrinova.local',
    'pbkdf2-sha256$120000$853OQt0HZkk/RLFJP2ZZGA==$w7+D8I+s3xKcOFhMJ6ZNlO3Om2JN3kXR2JzqhB5rC1U=',
    TRUE
)
ON CONFLICT (username) DO NOTHING;

SELECT set_config('app.role_policy_bypass', 'on', FALSE);

INSERT INTO sec_roles (code, name, is_super_role, is_active)
VALUES
    ('SUPER_ADMIN', 'Super Administrator', TRUE, TRUE),
    ('GL_ADMIN', 'General Ledger Admin', FALSE, TRUE),
    ('GL_STAFF', 'General Ledger Staff', FALSE, TRUE),
    ('GL_APPROVER', 'General Ledger Approver', FALSE, TRUE),
    ('INV_ADMIN', 'Inventory Admin', FALSE, TRUE),
    ('INV_STAFF', 'Inventory Staff', FALSE, TRUE),
    ('INV_APPROVER', 'Inventory Approver', FALSE, TRUE),
    ('FINANCE_ADMIN', 'Finance Admin', FALSE, TRUE)
ON CONFLICT (code) DO UPDATE
SET name = EXCLUDED.name,
    is_super_role = EXCLUDED.is_super_role,
    is_active = EXCLUDED.is_active,
    updated_at = NOW()
WHERE sec_roles.name IS DISTINCT FROM EXCLUDED.name
   OR sec_roles.is_super_role IS DISTINCT FROM EXCLUDED.is_super_role
   OR sec_roles.is_active IS DISTINCT FROM EXCLUDED.is_active;

INSERT INTO sec_role_action_access (role_id, action_id)
SELECT r.id, a.id
FROM sec_roles r
JOIN sec_actions a ON a.is_active = TRUE
JOIN sec_submodules sm ON sm.id = a.submodule_id AND sm.is_active = TRUE
JOIN sec_modules mo ON mo.id = sm.module_id AND mo.is_active = TRUE
WHERE r.code = 'GL_ADMIN'
  AND lower(mo.module_code) = 'accounting'
  AND lower(sm.submodule_code) <> 'user_management'
ON CONFLICT DO NOTHING;

INSERT INTO sec_role_action_access (role_id, action_id)
SELECT r.id, a.id
FROM sec_roles r
JOIN sec_actions a ON a.is_active = TRUE
JOIN sec_submodules sm ON sm.id = a.submodule_id AND sm.is_active = TRUE
JOIN sec_modules mo ON mo.id = sm.module_id AND mo.is_active = TRUE
WHERE r.code = 'FINANCE_ADMIN'
  AND lower(mo.module_code) = 'accounting'
  AND lower(sm.submodule_code) <> 'user_management'
ON CONFLICT DO NOTHING;

INSERT INTO sec_role_action_access (role_id, action_id)
SELECT r.id, a.id
FROM sec_roles r
JOIN sec_actions a ON a.is_active = TRUE
JOIN sec_submodules sm ON sm.id = a.submodule_id AND sm.is_active = TRUE
JOIN sec_modules mo ON mo.id = sm.module_id AND mo.is_active = TRUE
WHERE r.code = 'INV_ADMIN'
  AND lower(mo.module_code) = 'inventory'
ON CONFLICT DO NOTHING;

WITH role_permission_seeds(role_code, module_code, submodule_code, action_code) AS (
    VALUES
        ('GL_STAFF', 'accounting', 'dashboard', 'view'),
        ('GL_STAFF', 'accounting', 'master_data', 'view'),
        ('GL_STAFF', 'accounting', 'transactions', 'view'),
        ('GL_STAFF', 'accounting', 'transactions', 'create'),
        ('GL_STAFF', 'accounting', 'transactions', 'update'),
        ('GL_STAFF', 'accounting', 'transactions', 'submit'),
        ('GL_STAFF', 'accounting', 'transactions', 'import'),
        ('GL_STAFF', 'accounting', 'transactions', 'export'),
        ('GL_STAFF', 'accounting', 'reports', 'view'),
        ('GL_STAFF', 'accounting', 'reports', 'export'),
        ('GL_APPROVER', 'accounting', 'dashboard', 'view'),
        ('GL_APPROVER', 'accounting', 'master_data', 'view'),
        ('GL_APPROVER', 'accounting', 'transactions', 'view'),
        ('GL_APPROVER', 'accounting', 'transactions', 'approve'),
        ('GL_APPROVER', 'accounting', 'transactions', 'post'),
        ('GL_APPROVER', 'accounting', 'reports', 'view'),
        ('GL_APPROVER', 'accounting', 'reports', 'export'),
        ('INV_STAFF', 'inventory', 'dashboard', 'view'),
        ('INV_STAFF', 'inventory', 'item', 'view'),
        ('INV_STAFF', 'inventory', 'item', 'create'),
        ('INV_STAFF', 'inventory', 'item', 'update'),
        ('INV_STAFF', 'inventory', 'kategori', 'view'),
        ('INV_STAFF', 'inventory', 'kategori', 'create'),
        ('INV_STAFF', 'inventory', 'kategori', 'update'),
        ('INV_STAFF', 'inventory', 'satuan', 'view'),
        ('INV_STAFF', 'inventory', 'satuan', 'create'),
        ('INV_STAFF', 'inventory', 'satuan', 'update'),
        ('INV_STAFF', 'inventory', 'gudang', 'view'),
        ('INV_STAFF', 'inventory', 'gudang', 'create'),
        ('INV_STAFF', 'inventory', 'gudang', 'update'),
        ('INV_STAFF', 'inventory', 'stock_in', 'view'),
        ('INV_STAFF', 'inventory', 'stock_in', 'create'),
        ('INV_STAFF', 'inventory', 'stock_in', 'update'),
        ('INV_STAFF', 'inventory', 'stock_in', 'submit'),
        ('INV_STAFF', 'inventory', 'stock_out', 'view'),
        ('INV_STAFF', 'inventory', 'stock_out', 'create'),
        ('INV_STAFF', 'inventory', 'stock_out', 'update'),
        ('INV_STAFF', 'inventory', 'stock_out', 'submit'),
        ('INV_STAFF', 'inventory', 'transfer', 'view'),
        ('INV_STAFF', 'inventory', 'transfer', 'create'),
        ('INV_STAFF', 'inventory', 'transfer', 'update'),
        ('INV_STAFF', 'inventory', 'transfer', 'submit'),
        ('INV_STAFF', 'inventory', 'stock_opname', 'view'),
        ('INV_STAFF', 'inventory', 'stock_opname', 'create'),
        ('INV_STAFF', 'inventory', 'stock_opname', 'update'),
        ('INV_STAFF', 'inventory', 'stock_opname', 'submit'),
        ('INV_STAFF', 'inventory', 'stock_adjustment', 'view'),
        ('INV_STAFF', 'inventory', 'stock_adjustment', 'create'),
        ('INV_STAFF', 'inventory', 'stock_adjustment', 'update'),
        ('INV_STAFF', 'inventory', 'stock_adjustment', 'submit'),
        ('INV_STAFF', 'inventory', 'api_inv', 'download_import_template'),
        ('INV_STAFF', 'inventory', 'api_inv', 'import_master_data'),
        ('INV_STAFF', 'inventory', 'reports', 'view'),
        ('INV_STAFF', 'inventory', 'reports', 'export'),
        ('INV_APPROVER', 'inventory', 'dashboard', 'view'),
        ('INV_APPROVER', 'inventory', 'item', 'view'),
        ('INV_APPROVER', 'inventory', 'kategori', 'view'),
        ('INV_APPROVER', 'inventory', 'satuan', 'view'),
        ('INV_APPROVER', 'inventory', 'gudang', 'view'),
        ('INV_APPROVER', 'inventory', 'stock_in', 'view'),
        ('INV_APPROVER', 'inventory', 'stock_in', 'approve'),
        ('INV_APPROVER', 'inventory', 'stock_in', 'post'),
        ('INV_APPROVER', 'inventory', 'stock_out', 'view'),
        ('INV_APPROVER', 'inventory', 'stock_out', 'approve'),
        ('INV_APPROVER', 'inventory', 'stock_out', 'post'),
        ('INV_APPROVER', 'inventory', 'transfer', 'view'),
        ('INV_APPROVER', 'inventory', 'transfer', 'approve'),
        ('INV_APPROVER', 'inventory', 'transfer', 'post'),
        ('INV_APPROVER', 'inventory', 'stock_opname', 'view'),
        ('INV_APPROVER', 'inventory', 'stock_opname', 'approve'),
        ('INV_APPROVER', 'inventory', 'stock_opname', 'post'),
        ('INV_APPROVER', 'inventory', 'stock_adjustment', 'view'),
        ('INV_APPROVER', 'inventory', 'stock_adjustment', 'approve'),
        ('INV_APPROVER', 'inventory', 'stock_adjustment', 'post'),
        ('INV_APPROVER', 'inventory', 'reports', 'view'),
        ('INV_APPROVER', 'inventory', 'reports', 'export')
)
INSERT INTO sec_role_action_access (role_id, action_id)
SELECT r.id, a.id
FROM role_permission_seeds s
JOIN sec_roles r ON upper(r.code) = upper(s.role_code)
JOIN sec_modules mo ON lower(mo.module_code) = lower(s.module_code) AND mo.is_active = TRUE
JOIN sec_submodules sm ON sm.module_id = mo.id AND lower(sm.submodule_code) = lower(s.submodule_code) AND sm.is_active = TRUE
JOIN sec_actions a ON a.submodule_id = sm.id AND lower(a.action_code) = lower(s.action_code) AND a.is_active = TRUE
ON CONFLICT DO NOTHING;

INSERT INTO sec_modules (module_code, module_name, sort_order, is_active)
VALUES
    ('accounting', 'Accounting', 10, TRUE),
    ('inventory', 'Inventory', 20, TRUE),
    ('fixed_asset', 'Fixed Asset', 30, TRUE)
ON CONFLICT (module_code) DO UPDATE
SET module_name = EXCLUDED.module_name,
    sort_order = EXCLUDED.sort_order,
    is_active = EXCLUDED.is_active,
    updated_at = NOW();

WITH submodule_seeds(module_code, submodule_code, submodule_name, sort_order, is_active) AS (
    VALUES
        ('accounting', 'dashboard', 'Dashboard', 10, TRUE),
        ('accounting', 'master_data', 'Master Data', 20, TRUE),
        ('accounting', 'transactions', 'Transactions', 30, TRUE),
        ('accounting', 'reports', 'Reports', 40, TRUE),
        ('accounting', 'settings', 'Settings', 50, TRUE),
        ('accounting', 'user_management', 'User Management', 60, TRUE),
        ('inventory', 'dashboard', 'Dashboard', 10, TRUE),
        ('inventory', 'item', 'Item', 20, TRUE),
        ('inventory', 'kategori', 'Kategori', 30, TRUE),
        ('inventory', 'satuan', 'Satuan', 40, TRUE),
        ('inventory', 'gudang', 'Gudang', 50, TRUE),
        ('inventory', 'stock_in', 'Barang Masuk', 60, TRUE),
        ('inventory', 'stock_out', 'Barang Keluar', 70, TRUE),
        ('inventory', 'transfer', 'Transfer', 80, TRUE),
        ('inventory', 'stock_opname', 'Stock Opname', 90, TRUE),
        ('inventory', 'stock_adjustment', 'Stock Adjustment', 100, TRUE),
        ('inventory', 'reports', 'Reports', 110, TRUE),
        ('inventory', 'api_inv', 'API Inv', 120, TRUE),
        ('fixed_asset', 'dashboard', 'Dashboard', 10, TRUE),
        ('fixed_asset', 'asset_register', 'Asset Register', 20, TRUE),
        ('fixed_asset', 'depreciation', 'Depreciation', 30, TRUE),
        ('fixed_asset', 'disposal', 'Disposal', 40, TRUE),
        ('fixed_asset', 'reports', 'Reports', 50, TRUE),
        ('fixed_asset', 'settings', 'Settings', 60, TRUE)
)
INSERT INTO sec_submodules (module_id, submodule_code, submodule_name, sort_order, is_active)
SELECT m.id, s.submodule_code, s.submodule_name, s.sort_order, s.is_active
FROM submodule_seeds s
JOIN sec_modules m ON lower(m.module_code) = lower(s.module_code)
ON CONFLICT (module_id, submodule_code) DO UPDATE
SET submodule_name = EXCLUDED.submodule_name,
    sort_order = EXCLUDED.sort_order,
    is_active = EXCLUDED.is_active,
    updated_at = NOW();

WITH action_seeds(module_code, submodule_code, action_code, action_name, sort_order, is_active) AS (
    VALUES
        ('accounting', 'dashboard', 'view', 'View', 10, TRUE),
        ('accounting', 'master_data', 'view', 'View', 10, TRUE),
        ('accounting', 'master_data', 'create', 'Create', 20, TRUE),
        ('accounting', 'master_data', 'update', 'Update', 30, TRUE),
        ('accounting', 'master_data', 'delete', 'Delete', 40, TRUE),
        ('accounting', 'master_data', 'import', 'Import', 50, TRUE),
        ('accounting', 'master_data', 'export', 'Export', 60, TRUE),
        ('accounting', 'transactions', 'view', 'View', 10, TRUE),
        ('accounting', 'transactions', 'create', 'Create', 20, TRUE),
        ('accounting', 'transactions', 'update', 'Update', 30, TRUE),
        ('accounting', 'transactions', 'delete', 'Delete', 40, TRUE),
        ('accounting', 'transactions', 'submit', 'Submit', 50, TRUE),
        ('accounting', 'transactions', 'approve', 'Approve', 60, TRUE),
        ('accounting', 'transactions', 'post', 'Post', 70, TRUE),
        ('accounting', 'transactions', 'import', 'Import', 80, TRUE),
        ('accounting', 'transactions', 'export', 'Export', 90, TRUE),
        ('accounting', 'reports', 'view', 'View', 10, TRUE),
        ('accounting', 'reports', 'export', 'Export', 20, TRUE),
        ('accounting', 'settings', 'view', 'View', 10, TRUE),
        ('accounting', 'settings', 'update', 'Update', 20, TRUE),
        ('accounting', 'user_management', 'view', 'View', 10, TRUE),
        ('accounting', 'user_management', 'create', 'Create', 20, TRUE),
        ('accounting', 'user_management', 'update', 'Update', 30, TRUE),
        ('accounting', 'user_management', 'delete', 'Delete', 40, TRUE),
        ('accounting', 'user_management', 'manage_roles', 'Manage Roles', 50, TRUE),
        ('accounting', 'user_management', 'manage_companies', 'Manage Companies', 60, TRUE),
        ('accounting', 'user_management', 'manage_locations', 'Manage Locations', 70, TRUE),
        ('inventory', 'dashboard', 'view', 'View', 10, TRUE),
        ('inventory', 'item', 'view', 'View', 10, TRUE),
        ('inventory', 'item', 'create', 'Create', 20, TRUE),
        ('inventory', 'item', 'update', 'Update', 30, TRUE),
        ('inventory', 'item', 'delete', 'Delete', 40, TRUE),
        ('inventory', 'kategori', 'view', 'View', 10, TRUE),
        ('inventory', 'kategori', 'create', 'Create', 20, TRUE),
        ('inventory', 'kategori', 'update', 'Update', 30, TRUE),
        ('inventory', 'kategori', 'delete', 'Delete', 40, TRUE),
        ('inventory', 'satuan', 'view', 'View', 10, TRUE),
        ('inventory', 'satuan', 'create', 'Create', 20, TRUE),
        ('inventory', 'satuan', 'update', 'Update', 30, TRUE),
        ('inventory', 'satuan', 'delete', 'Delete', 40, TRUE),
        ('inventory', 'gudang', 'view', 'View', 10, TRUE),
        ('inventory', 'gudang', 'create', 'Create', 20, TRUE),
        ('inventory', 'gudang', 'update', 'Update', 30, TRUE),
        ('inventory', 'gudang', 'delete', 'Delete', 40, TRUE),
        ('inventory', 'stock_in', 'view', 'View', 10, TRUE),
        ('inventory', 'stock_in', 'create', 'Create', 20, TRUE),
        ('inventory', 'stock_in', 'update', 'Update', 30, TRUE),
        ('inventory', 'stock_in', 'submit', 'Submit', 40, TRUE),
        ('inventory', 'stock_in', 'approve', 'Approve', 50, TRUE),
        ('inventory', 'stock_in', 'post', 'Post', 60, TRUE),
        ('inventory', 'stock_out', 'view', 'View', 10, TRUE),
        ('inventory', 'stock_out', 'create', 'Create', 20, TRUE),
        ('inventory', 'stock_out', 'update', 'Update', 30, TRUE),
        ('inventory', 'stock_out', 'submit', 'Submit', 40, TRUE),
        ('inventory', 'stock_out', 'approve', 'Approve', 50, TRUE),
        ('inventory', 'stock_out', 'post', 'Post', 60, TRUE),
        ('inventory', 'transfer', 'view', 'View', 10, TRUE),
        ('inventory', 'transfer', 'create', 'Create', 20, TRUE),
        ('inventory', 'transfer', 'update', 'Update', 30, TRUE),
        ('inventory', 'transfer', 'submit', 'Submit', 40, TRUE),
        ('inventory', 'transfer', 'approve', 'Approve', 50, TRUE),
        ('inventory', 'transfer', 'post', 'Post', 60, TRUE),
        ('inventory', 'stock_opname', 'view', 'View', 10, TRUE),
        ('inventory', 'stock_opname', 'create', 'Create', 20, TRUE),
        ('inventory', 'stock_opname', 'update', 'Update', 30, TRUE),
        ('inventory', 'stock_opname', 'submit', 'Submit', 40, TRUE),
        ('inventory', 'stock_opname', 'approve', 'Approve', 50, TRUE),
        ('inventory', 'stock_opname', 'post', 'Post', 60, TRUE),
        ('inventory', 'stock_adjustment', 'view', 'View', 10, TRUE),
        ('inventory', 'stock_adjustment', 'create', 'Create', 20, TRUE),
        ('inventory', 'stock_adjustment', 'update', 'Update', 30, TRUE),
        ('inventory', 'stock_adjustment', 'submit', 'Submit', 40, TRUE),
        ('inventory', 'stock_adjustment', 'approve', 'Approve', 50, TRUE),
        ('inventory', 'stock_adjustment', 'post', 'Post', 60, TRUE),
        ('inventory', 'reports', 'view', 'View', 10, TRUE),
        ('inventory', 'reports', 'export', 'Export', 20, TRUE),
        ('inventory', 'api_inv', 'view', 'View', 10, TRUE),
        ('inventory', 'api_inv', 'update_settings', 'Update Settings', 20, TRUE),
        ('inventory', 'api_inv', 'manage_master_company', 'Manage Master Company', 30, TRUE),
        ('inventory', 'api_inv', 'sync_upload', 'Sync Upload', 40, TRUE),
        ('inventory', 'api_inv', 'sync_download', 'Sync Download', 50, TRUE),
        ('inventory', 'api_inv', 'download_import_template', 'Download Import Template', 55, TRUE),
        ('inventory', 'api_inv', 'import_master_data', 'Import Master Data', 60, TRUE),
        ('inventory', 'api_inv', 'pull_journal', 'Pull Journal', 70, TRUE),
        ('fixed_asset', 'dashboard', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'asset_register', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'asset_register', 'create', 'Create', 20, TRUE),
        ('fixed_asset', 'asset_register', 'update', 'Update', 30, TRUE),
        ('fixed_asset', 'asset_register', 'delete', 'Delete', 40, TRUE),
        ('fixed_asset', 'depreciation', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'depreciation', 'generate', 'Generate', 20, TRUE),
        ('fixed_asset', 'depreciation', 'post', 'Post', 30, TRUE),
        ('fixed_asset', 'disposal', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'disposal', 'create', 'Create', 20, TRUE),
        ('fixed_asset', 'disposal', 'approve', 'Approve', 30, TRUE),
        ('fixed_asset', 'reports', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'reports', 'export', 'Export', 20, TRUE),
        ('fixed_asset', 'settings', 'view', 'View', 10, TRUE),
        ('fixed_asset', 'settings', 'update', 'Update', 20, TRUE)
)
INSERT INTO sec_actions (submodule_id, action_code, action_name, sort_order, is_active)
SELECT sm.id, s.action_code, s.action_name, s.sort_order, s.is_active
FROM action_seeds s
JOIN sec_modules m ON lower(m.module_code) = lower(s.module_code)
JOIN sec_submodules sm ON sm.module_id = m.id AND lower(sm.submodule_code) = lower(s.submodule_code)
ON CONFLICT (submodule_id, action_code) DO UPDATE
SET action_name = EXCLUDED.action_name,
    sort_order = EXCLUDED.sort_order,
    is_active = EXCLUDED.is_active,
    updated_at = NOW();

DO $$
BEGIN
    IF to_regclass('sec_role_scope_access') IS NOT NULL
       AND to_regclass('sec_access_scopes') IS NOT NULL THEN
        EXECUTE $migration$
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
        $migration$;
    END IF;
END
$$;

SELECT set_config('app.role_policy_bypass', 'off', FALSE);

INSERT INTO org_companies (code, name, is_active)
VALUES ('AGRINOVA', 'Agrinova Main Company', TRUE)
ON CONFLICT (code) DO NOTHING;

DO $$
BEGIN
    UPDATE org_locations l
    SET code = 'HO',
        name = 'Head Office',
        updated_at = NOW()
    FROM org_companies c
    WHERE l.company_id = c.id
      AND c.code = 'AGRINOVA'
      AND UPPER(l.code) = 'HQ'
      AND NOT EXISTS (
          SELECT 1
          FROM org_locations x
          WHERE x.company_id = l.company_id
            AND UPPER(x.code) = 'HO'
      );
END
$$;

INSERT INTO org_locations (company_id, code, name, location_type, is_active)
SELECT c.id, v.code, v.name, v.location_type, TRUE
FROM org_companies c
JOIN (
    VALUES
        ('HO', 'Head Office', 'OFFICE'),
        ('PK', 'PKS', 'MILL'),
        ('KB', 'Kebun', 'ESTATE')
) AS v(code, name, location_type)
    ON TRUE
WHERE c.code = 'AGRINOVA'
ON CONFLICT (company_id, code) DO NOTHING;

UPDATE app_users u
SET default_company_id = NULL
WHERE default_company_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM org_companies c WHERE c.id = u.default_company_id
  );

UPDATE app_users u
SET default_location_id = NULL
WHERE default_location_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM org_locations l WHERE l.id = u.default_location_id
  );

UPDATE app_users u
SET default_location_id = NULL
WHERE default_location_id IS NOT NULL
  AND default_company_id IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM org_locations l
      WHERE l.id = u.default_location_id
        AND l.company_id = u.default_company_id
  );

INSERT INTO sec_user_roles (user_id, role_id)
SELECT u.id, r.id
FROM app_users u
JOIN sec_roles r ON r.code = 'SUPER_ADMIN'
WHERE lower(u.username) = 'admin'
ON CONFLICT DO NOTHING;
