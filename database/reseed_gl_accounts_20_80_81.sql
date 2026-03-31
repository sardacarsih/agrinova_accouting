BEGIN;

-- Idempotent reseed sourced from MASTER AKUN BLOK COST CENTRE RESEED 20 80 81 LVL1.xlsx
-- for dedicated plantation business classifications in gl_accounts.
--
-- This script upserts the requested hierarchy into every active company.
-- It does not delete existing journals or unrelated GL accounts.

ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS hierarchy_level INT NOT NULL DEFAULT 1;

CREATE TEMP TABLE tmp_reseed_gl_account_templates (
    level_no INT NOT NULL,
    account_code VARCHAR(20) NOT NULL,
    parent_code VARCHAR(20) NULL,
    account_name VARCHAR(200) NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    normal_balance CHAR(1) NOT NULL,
    sort_order INT NOT NULL,
    report_group VARCHAR(50) NOT NULL,
    requires_cost_center BOOLEAN NOT NULL DEFAULT FALSE
) ON COMMIT DROP;

INSERT INTO tmp_reseed_gl_account_templates (
    level_no,
    account_code,
    parent_code,
    account_name,
    account_type,
    normal_balance,
    sort_order,
    report_group,
    requires_cost_center
)
VALUES
    (1, '20.00000.000', NULL, 'TANAMAN BELUM MENGHASILKAN', 'ASSET', 'D', 20000, 'BS_ASSET', FALSE),
    (4, '20.00000.600', '20.00000.000', 'KONSERVASI, KONSOLIDASI DAN SENSUS', 'ASSET', 'D', 20600, 'BS_ASSET', FALSE),
    (5, '20.00000.607', '20.00000.600', 'Reseed Kelapa Sawit - Material', 'ASSET', 'D', 20607, 'BS_ASSET', TRUE),
    (5, '20.00000.608', '20.00000.600', 'Reseed Kelapa Sawit - Labour', 'ASSET', 'D', 20608, 'BS_ASSET', TRUE),
    (5, '20.00000.609', '20.00000.600', 'Reseed Kelapa Sawit - Kontraktor', 'ASSET', 'D', 20609, 'BS_ASSET', TRUE),
    (1, '80.00000.000', NULL, 'BIAYA PANEN', 'EXPENSE', 'D', 80000, 'PL_EXPENSE', FALSE),
    (4, '80.00000.600', '80.00000.000', 'BIAYA ALOKASI HASIL PANEN', 'EXPENSE', 'D', 80600, 'PL_EXPENSE', FALSE),
    (5, '80.00000.607', '80.00000.600', 'Reseed Kelapa Sawit - Material', 'EXPENSE', 'D', 80607, 'PL_EXPENSE', TRUE),
    (5, '80.00000.608', '80.00000.600', 'Reseed Kelapa Sawit - Labour', 'EXPENSE', 'D', 80608, 'PL_EXPENSE', TRUE),
    (5, '80.00000.609', '80.00000.600', 'Reseed Kelapa Sawit - Kontraktor', 'EXPENSE', 'D', 80609, 'PL_EXPENSE', TRUE),
    (1, '81.00000.000', NULL, 'TANAMAN MENGHASILKAN', 'EXPENSE', 'D', 81000, 'PL_EXPENSE', FALSE),
    (4, '81.00000.600', '81.00000.000', 'KONSERVASI, KONSOLIDASI & SENSUS', 'EXPENSE', 'D', 81600, 'PL_EXPENSE', FALSE),
    (5, '81.00000.607', '81.00000.600', 'Reseed Kelapa Sawit - Material', 'EXPENSE', 'D', 81607, 'PL_EXPENSE', TRUE),
    (5, '81.00000.608', '81.00000.600', 'Reseed Kelapa Sawit - Labour', 'EXPENSE', 'D', 81608, 'PL_EXPENSE', TRUE),
    (5, '81.00000.609', '81.00000.600', 'Reseed Kelapa Sawit - Kontraktor', 'EXPENSE', 'D', 81609, 'PL_EXPENSE', TRUE);

WITH active_companies AS (
    SELECT id AS company_id
    FROM org_companies
    WHERE is_active = TRUE
)
INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    hierarchy_level,
    is_posting,
    is_active,
    sort_order,
    report_group,
    requires_department,
    requires_project,
    requires_cost_center,
    created_by,
    updated_by,
    created_at,
    updated_at
)
SELECT c.company_id,
       t.account_code,
       t.account_name,
       t.account_type,
       t.normal_balance,
       NULL,
       t.level_no,
       FALSE,
       TRUE,
       t.sort_order,
       t.report_group,
       FALSE,
       FALSE,
       t.requires_cost_center,
       'SEED',
       'SEED',
       NOW(),
       NOW()
FROM active_companies c
JOIN tmp_reseed_gl_account_templates t ON t.level_no = 1
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = NULL,
    hierarchy_level = EXCLUDED.hierarchy_level,
    is_posting = FALSE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    requires_department = FALSE,
    requires_project = FALSE,
    requires_cost_center = EXCLUDED.requires_cost_center,
    updated_by = 'SEED',
    updated_at = NOW();

WITH active_companies AS (
    SELECT id AS company_id
    FROM org_companies
    WHERE is_active = TRUE
)
INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    hierarchy_level,
    is_posting,
    is_active,
    sort_order,
    report_group,
    requires_department,
    requires_project,
    requires_cost_center,
    created_by,
    updated_by,
    created_at,
    updated_at
)
SELECT c.company_id,
       t.account_code,
       t.account_name,
       t.account_type,
       t.normal_balance,
       p.id,
       t.level_no,
       FALSE,
       TRUE,
       t.sort_order,
       t.report_group,
       FALSE,
       FALSE,
       t.requires_cost_center,
       'SEED',
       'SEED',
       NOW(),
       NOW()
FROM active_companies c
JOIN tmp_reseed_gl_account_templates t ON t.level_no = 4
JOIN gl_accounts p
  ON p.company_id = c.company_id
 AND p.account_code = t.parent_code
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = EXCLUDED.parent_account_id,
    hierarchy_level = EXCLUDED.hierarchy_level,
    is_posting = FALSE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    requires_department = FALSE,
    requires_project = FALSE,
    requires_cost_center = EXCLUDED.requires_cost_center,
    updated_by = 'SEED',
    updated_at = NOW();

WITH active_companies AS (
    SELECT id AS company_id
    FROM org_companies
    WHERE is_active = TRUE
)
INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    hierarchy_level,
    is_posting,
    is_active,
    sort_order,
    report_group,
    requires_department,
    requires_project,
    requires_cost_center,
    created_by,
    updated_by,
    created_at,
    updated_at
)
SELECT c.company_id,
       t.account_code,
       t.account_name,
       t.account_type,
       t.normal_balance,
       p.id,
       t.level_no,
       TRUE,
       TRUE,
       t.sort_order,
       t.report_group,
       FALSE,
       FALSE,
       t.requires_cost_center,
       'SEED',
       'SEED',
       NOW(),
       NOW()
FROM active_companies c
JOIN tmp_reseed_gl_account_templates t ON t.level_no = 5
JOIN gl_accounts p
  ON p.company_id = c.company_id
 AND p.account_code = t.parent_code
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = EXCLUDED.parent_account_id,
    hierarchy_level = EXCLUDED.hierarchy_level,
    is_posting = TRUE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    requires_department = FALSE,
    requires_project = FALSE,
    requires_cost_center = EXCLUDED.requires_cost_center,
    updated_by = 'SEED',
    updated_at = NOW();

UPDATE gl_accounts a
SET is_posting = NOT EXISTS (
        SELECT 1
        FROM gl_accounts c
        WHERE c.parent_account_id = a.id
    ),
    updated_by = 'SEED',
    updated_at = NOW()
WHERE EXISTS (
    SELECT 1
    FROM tmp_reseed_gl_account_templates t
    WHERE t.account_code = a.account_code
);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'gl_accounts'
          AND column_name = 'account_level'
    ) THEN
        UPDATE gl_accounts a
        SET account_level = t.level_no
        FROM tmp_reseed_gl_account_templates t
        WHERE a.account_code = t.account_code
          AND a.account_level IS DISTINCT FROM t.level_no;
    END IF;
END
$$;

COMMIT;
