using Npgsql;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private async Task EnsureJournalSchemaAsync(CancellationToken cancellationToken)
    {
        if (_journalSchemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_journalSchemaEnsured)
            {
                return;
            }

            const string sql = @"
CREATE TABLE IF NOT EXISTS gl_accounts (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    account_code VARCHAR(80) NOT NULL,
    account_name VARCHAR(200) NOT NULL,
    account_type VARCHAR(20) NOT NULL DEFAULT 'ASSET',
    normal_balance CHAR(1) NOT NULL DEFAULT 'D',
    parent_account_id BIGINT NULL REFERENCES gl_accounts(id) ON DELETE SET NULL,
    hierarchy_level INT NOT NULL DEFAULT 1,
    is_posting BOOLEAN NOT NULL DEFAULT TRUE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    sort_order INT NOT NULL DEFAULT 100,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_gl_accounts_type CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    CONSTRAINT chk_gl_accounts_normal_balance CHECK (normal_balance IN ('D','C'))
);

ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS company_id BIGINT NULL REFERENCES org_companies(id) ON DELETE RESTRICT;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS account_code VARCHAR(80);
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS account_name VARCHAR(200);
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS account_type VARCHAR(20) NOT NULL DEFAULT 'ASSET';
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS normal_balance CHAR(1) NOT NULL DEFAULT 'D';
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS parent_account_id BIGINT NULL REFERENCES gl_accounts(id) ON DELETE SET NULL;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS hierarchy_level INT NOT NULL DEFAULT 1;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS is_posting BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS sort_order INT NOT NULL DEFAULT 100;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS report_group VARCHAR(50);
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS cashflow_category VARCHAR(50);
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS requires_department BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS requires_project BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS requires_cost_center BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM';
ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS updated_by VARCHAR(100) NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'gl_accounts'
          AND column_name = 'code'
    ) THEN
        UPDATE gl_accounts
        SET account_code = code
        WHERE account_code IS NULL OR btrim(account_code) = '';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'gl_accounts'
          AND column_name = 'name'
    ) THEN
        UPDATE gl_accounts
        SET account_name = name
        WHERE account_name IS NULL OR btrim(account_name) = '';
    END IF;
END
$$;

UPDATE gl_accounts
SET account_code = upper(btrim(account_code))
WHERE account_code IS NOT NULL;

UPDATE gl_accounts
SET account_name = btrim(account_name)
WHERE account_name IS NOT NULL;

DO $$
DECLARE
    v_default_company_id BIGINT;
BEGIN
    SELECT id
    INTO v_default_company_id
    FROM org_companies
    ORDER BY id
    LIMIT 1;

    IF v_default_company_id IS NULL THEN
        RAISE EXCEPTION 'org_companies has no rows. Seed company data first.';
    END IF;

    UPDATE gl_accounts
    SET company_id = v_default_company_id
    WHERE company_id IS NULL;
END
$$;

ALTER TABLE gl_accounts
    ALTER COLUMN company_id SET NOT NULL,
    ALTER COLUMN account_code SET NOT NULL,
    ALTER COLUMN account_name SET NOT NULL;

ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS gl_accounts_code_key;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_type;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_normal_balance;

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_type
    CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE'));

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_normal_balance
    CHECK (normal_balance IN ('D','C'));

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_accounts_company_code'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT uq_gl_accounts_company_code UNIQUE (company_id, account_code);
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'chk_gl_accounts_type_balance'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT chk_gl_accounts_type_balance
            CHECK (
                (account_type IN ('ASSET','EXPENSE') AND normal_balance = 'D')
                OR
                (account_type IN ('LIABILITY','EQUITY','REVENUE') AND normal_balance = 'C')
            );
    END IF;
END
$$;

UPDATE gl_accounts
SET account_type = CASE
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '1' THEN 'ASSET'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '2' THEN 'LIABILITY'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '3' THEN 'EQUITY'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '4' THEN 'REVENUE'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '5' THEN 'EXPENSE'
    WHEN left(account_code, 1) = '1' THEN 'ASSET'
    WHEN left(account_code, 1) = '2' THEN 'LIABILITY'
    WHEN left(account_code, 1) = '3' THEN 'EQUITY'
    WHEN left(account_code, 1) = '4' THEN 'REVENUE'
    WHEN left(account_code, 1) = '5' THEN 'EXPENSE'
    ELSE 'ASSET'
END
WHERE account_type IS NULL
   OR account_type = ''
   OR account_type NOT IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE');

UPDATE gl_accounts
SET normal_balance = CASE
    WHEN account_type IN ('LIABILITY', 'EQUITY', 'REVENUE') THEN 'C'
    ELSE 'D'
END
WHERE normal_balance IS NULL
   OR normal_balance = ''
   OR normal_balance NOT IN ('D', 'C');

UPDATE gl_accounts
SET hierarchy_level = CASE
    WHEN parent_account_id IS NULL THEN 1
    WHEN EXISTS (
        SELECT 1
        FROM gl_accounts p
        WHERE p.id = gl_accounts.parent_account_id
          AND p.parent_account_id IS NOT NULL
    ) THEN 3
    ELSE 2
END
WHERE hierarchy_level IS DISTINCT FROM CASE
    WHEN parent_account_id IS NULL THEN 1
    WHEN EXISTS (
        SELECT 1
        FROM gl_accounts p
        WHERE p.id = gl_accounts.parent_account_id
          AND p.parent_account_id IS NOT NULL
    ) THEN 3
    ELSE 2
END;

UPDATE gl_accounts
SET is_posting = NOT EXISTS (
    SELECT 1
    FROM gl_accounts c
    WHERE c.parent_account_id = gl_accounts.id)
WHERE is_posting IS DISTINCT FROM NOT EXISTS (
    SELECT 1
    FROM gl_accounts c
    WHERE c.parent_account_id = gl_accounts.id);

CREATE TABLE IF NOT EXISTS gl_accounting_periods (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    period_month DATE NOT NULL,
    is_open BOOLEAN NOT NULL DEFAULT TRUE,
    closed_at TIMESTAMPTZ NULL,
    closed_by VARCHAR(100) NULL,
    note VARCHAR(400) NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_gl_accounting_period_scope UNIQUE (company_id, location_id, period_month)
);

CREATE TABLE IF NOT EXISTS gl_cost_centers (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    parent_id BIGINT NULL REFERENCES gl_cost_centers(id) ON DELETE RESTRICT,
    cost_center_code VARCHAR(80) NOT NULL,
    cost_center_name VARCHAR(200) NOT NULL DEFAULT '',
    estate_code VARCHAR(40) NOT NULL DEFAULT '',
    estate_name VARCHAR(120) NOT NULL DEFAULT '',
    division_code VARCHAR(40) NOT NULL DEFAULT '',
    division_name VARCHAR(120) NOT NULL DEFAULT '',
    block_code VARCHAR(40) NOT NULL DEFAULT '',
    block_name VARCHAR(120) NOT NULL DEFAULT '',
    level VARCHAR(20) NOT NULL DEFAULT 'BLOCK',
    is_posting BOOLEAN NOT NULL DEFAULT TRUE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS company_id BIGINT NULL REFERENCES org_companies(id) ON DELETE CASCADE;
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS location_id BIGINT NULL REFERENCES org_locations(id) ON DELETE CASCADE;
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS parent_id BIGINT NULL REFERENCES gl_cost_centers(id) ON DELETE RESTRICT;
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS cost_center_code VARCHAR(80);
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS cost_center_name VARCHAR(200) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS estate_code VARCHAR(40) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS estate_name VARCHAR(120) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS division_code VARCHAR(40) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS division_name VARCHAR(120) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS block_code VARCHAR(40) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS block_name VARCHAR(120) NOT NULL DEFAULT '';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS level VARCHAR(20) NOT NULL DEFAULT 'BLOCK';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS is_posting BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM';
ALTER TABLE gl_cost_centers
    ADD COLUMN IF NOT EXISTS updated_by VARCHAR(100) NULL;

UPDATE gl_cost_centers
SET cost_center_code = upper(btrim(cost_center_code))
WHERE cost_center_code IS NOT NULL;

UPDATE gl_cost_centers
SET estate_code = upper(btrim(estate_code)),
    division_code = upper(btrim(division_code)),
    block_code = upper(btrim(block_code)),
    estate_name = btrim(estate_name),
    division_name = btrim(division_name),
    block_name = btrim(block_name),
    cost_center_name = btrim(cost_center_name),
    level = upper(btrim(level));

UPDATE gl_cost_centers
SET level = CASE
    WHEN btrim(coalesce(estate_code, '')) <> ''
         AND btrim(coalesce(division_code, '')) = ''
         AND btrim(coalesce(block_code, '')) = '' THEN 'ESTATE'
    WHEN btrim(coalesce(estate_code, '')) <> ''
         AND btrim(coalesce(division_code, '')) <> ''
         AND btrim(coalesce(block_code, '')) = '' THEN 'DIVISION'
    WHEN btrim(coalesce(estate_code, '')) <> ''
         AND btrim(coalesce(division_code, '')) <> ''
         AND btrim(coalesce(block_code, '')) <> '' THEN 'BLOCK'
    ELSE 'BLOCK'
END;

UPDATE gl_cost_centers
SET cost_center_code = CASE
    WHEN level = 'ESTATE' THEN estate_code
    WHEN level = 'DIVISION' THEN estate_code || '-' || division_code
    ELSE estate_code || '-' || division_code || '-' || block_code
END
WHERE cost_center_code IS NULL OR btrim(cost_center_code) = '';

UPDATE gl_cost_centers
SET is_posting = CASE WHEN level = 'BLOCK' THEN TRUE ELSE FALSE END
WHERE is_posting IS DISTINCT FROM CASE WHEN level = 'BLOCK' THEN TRUE ELSE FALSE END;

ALTER TABLE gl_cost_centers
    ALTER COLUMN company_id SET NOT NULL,
    ALTER COLUMN location_id SET NOT NULL,
    ALTER COLUMN cost_center_code SET NOT NULL;

ALTER TABLE gl_cost_centers DROP CONSTRAINT IF EXISTS chk_gl_cost_centers_level;
ALTER TABLE gl_cost_centers DROP CONSTRAINT IF EXISTS chk_gl_cost_centers_code_not_blank;
ALTER TABLE gl_cost_centers DROP CONSTRAINT IF EXISTS chk_gl_cost_centers_estate_required;
ALTER TABLE gl_cost_centers DROP CONSTRAINT IF EXISTS chk_gl_cost_centers_level_shape;
ALTER TABLE gl_cost_centers DROP CONSTRAINT IF EXISTS chk_gl_cost_centers_parent_not_self;

ALTER TABLE gl_cost_centers
    ADD CONSTRAINT chk_gl_cost_centers_level
    CHECK (level IN ('ESTATE','DIVISION','BLOCK'));

ALTER TABLE gl_cost_centers
    ADD CONSTRAINT chk_gl_cost_centers_code_not_blank
    CHECK (btrim(cost_center_code) <> '');

ALTER TABLE gl_cost_centers
    ADD CONSTRAINT chk_gl_cost_centers_estate_required
    CHECK (btrim(estate_code) <> '');

ALTER TABLE gl_cost_centers
    ADD CONSTRAINT chk_gl_cost_centers_level_shape
    CHECK (
        (level = 'ESTATE' AND btrim(division_code) = '' AND btrim(block_code) = '')
        OR (level = 'DIVISION' AND btrim(division_code) <> '' AND btrim(block_code) = '')
        OR (level = 'BLOCK' AND btrim(division_code) <> '' AND btrim(block_code) <> '')
    );

ALTER TABLE gl_cost_centers
    ADD CONSTRAINT chk_gl_cost_centers_parent_not_self
    CHECK (parent_id IS NULL OR parent_id <> id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_cost_centers_scope_code'
          AND conrelid = 'gl_cost_centers'::regclass
    ) THEN
        ALTER TABLE gl_cost_centers
            ADD CONSTRAINT uq_gl_cost_centers_scope_code
            UNIQUE (company_id, location_id, cost_center_code);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_cost_centers_scope_natural'
          AND conrelid = 'gl_cost_centers'::regclass
    ) THEN
        ALTER TABLE gl_cost_centers
            ADD CONSTRAINT uq_gl_cost_centers_scope_natural
            UNIQUE (company_id, location_id, estate_code, division_code, block_code);
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS gl_journal_headers (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    journal_no VARCHAR(80) NOT NULL,
    journal_date DATE NOT NULL,
    period_month DATE NOT NULL DEFAULT DATE_TRUNC('month', CURRENT_DATE)::DATE,
    reference_no VARCHAR(120) NOT NULL DEFAULT '',
    description VARCHAR(500) NOT NULL DEFAULT '',
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    approved_at TIMESTAMPTZ NULL,
    approved_by VARCHAR(100) NULL,
    posted_at TIMESTAMPTZ NULL,
    posted_by VARCHAR(100) NULL,
    created_by VARCHAR(100) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_gl_journal_company_location_period_no UNIQUE (company_id, location_id, period_month, journal_no),
    CONSTRAINT chk_gl_journal_status CHECK (status IN ('DRAFT','SUBMITTED','APPROVED','POSTED'))
);

ALTER TABLE gl_journal_headers
    ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ NULL;

ALTER TABLE gl_journal_headers
    ADD COLUMN IF NOT EXISTS approved_by VARCHAR(100) NULL;

ALTER TABLE gl_journal_headers
    ADD COLUMN IF NOT EXISTS period_month DATE;

UPDATE gl_journal_headers
SET period_month = DATE_TRUNC('month', journal_date)::DATE
WHERE period_month IS NULL;

ALTER TABLE gl_journal_headers
    ALTER COLUMN period_month SET NOT NULL;

ALTER TABLE gl_journal_headers
    ALTER COLUMN period_month SET DEFAULT DATE_TRUNC('month', CURRENT_DATE)::DATE;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_journal_company_location_no'
          AND conrelid = 'gl_journal_headers'::regclass
    ) THEN
        ALTER TABLE gl_journal_headers
            DROP CONSTRAINT uq_gl_journal_company_location_no;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_journal_company_location_period_no'
          AND conrelid = 'gl_journal_headers'::regclass
    ) THEN
        ALTER TABLE gl_journal_headers
            ADD CONSTRAINT uq_gl_journal_company_location_period_no
            UNIQUE (company_id, location_id, period_month, journal_no);
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'chk_gl_journal_status'
    ) THEN
        ALTER TABLE gl_journal_headers
            DROP CONSTRAINT chk_gl_journal_status;
    END IF;

    ALTER TABLE gl_journal_headers
        ADD CONSTRAINT chk_gl_journal_status
        CHECK (status IN ('DRAFT','SUBMITTED','APPROVED','POSTED'));
END
$$;

CREATE TABLE IF NOT EXISTS gl_journal_details (
    id BIGSERIAL PRIMARY KEY,
    header_id BIGINT NOT NULL REFERENCES gl_journal_headers(id) ON DELETE CASCADE,
    line_no INT NOT NULL,
    account_id BIGINT NOT NULL REFERENCES gl_accounts(id) ON DELETE RESTRICT,
    description VARCHAR(500) NOT NULL DEFAULT '',
    debit NUMERIC(18,2) NOT NULL DEFAULT 0,
    credit NUMERIC(18,2) NOT NULL DEFAULT 0,
    department_code VARCHAR(80) NOT NULL DEFAULT '',
    project_code VARCHAR(80) NOT NULL DEFAULT '',
    cost_center_id BIGINT NULL REFERENCES gl_cost_centers(id) ON DELETE RESTRICT,
    cost_center_code VARCHAR(80) NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_gl_journal_detail_line UNIQUE (header_id, line_no),
    CONSTRAINT chk_gl_journal_amount_non_negative CHECK (debit >= 0 AND credit >= 0),
    CONSTRAINT chk_gl_journal_amount_one_side CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))
);

CREATE TABLE IF NOT EXISTS gl_ledger_entries (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    period_month DATE NOT NULL,
    journal_id BIGINT NOT NULL REFERENCES gl_journal_headers(id) ON DELETE CASCADE,
    journal_no VARCHAR(80) NOT NULL,
    journal_date DATE NOT NULL,
    journal_line_no INT NOT NULL,
    account_id BIGINT NOT NULL REFERENCES gl_accounts(id) ON DELETE RESTRICT,
    debit NUMERIC(18,2) NOT NULL DEFAULT 0,
    credit NUMERIC(18,2) NOT NULL DEFAULT 0,
    description VARCHAR(500) NOT NULL DEFAULT '',
    department_code VARCHAR(80) NOT NULL DEFAULT '',
    project_code VARCHAR(80) NOT NULL DEFAULT '',
    cost_center_id BIGINT NULL REFERENCES gl_cost_centers(id) ON DELETE RESTRICT,
    cost_center_code VARCHAR(80) NOT NULL DEFAULT '',
    posted_by VARCHAR(100) NOT NULL,
    posted_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_gl_ledger_journal_line UNIQUE (journal_id, journal_line_no),
    CONSTRAINT chk_gl_ledger_amount_non_negative CHECK (debit >= 0 AND credit >= 0),
    CONSTRAINT chk_gl_ledger_amount_one_side CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))
);

CREATE INDEX IF NOT EXISTS idx_gl_journal_headers_scope_date
    ON gl_journal_headers(company_id, location_id, journal_date DESC, id DESC);

CREATE INDEX IF NOT EXISTS idx_gl_journal_headers_status
    ON gl_journal_headers(status);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_parent
    ON gl_accounts(company_id, parent_account_id);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_type_code
    ON gl_accounts(company_id, account_type, account_code);

CREATE INDEX IF NOT EXISTS idx_gl_cost_centers_scope_level
    ON gl_cost_centers(company_id, location_id, level, cost_center_code);

CREATE INDEX IF NOT EXISTS idx_gl_cost_centers_parent
    ON gl_cost_centers(parent_id);

CREATE INDEX IF NOT EXISTS idx_gl_accounting_period_scope
    ON gl_accounting_periods(company_id, location_id, period_month);

ALTER TABLE gl_journal_details
    ADD COLUMN IF NOT EXISTS cost_center_id BIGINT NULL;

ALTER TABLE gl_ledger_entries
    ADD COLUMN IF NOT EXISTS cost_center_id BIGINT NULL;

CREATE INDEX IF NOT EXISTS idx_gl_ledger_scope_period_account
    ON gl_ledger_entries(company_id, location_id, period_month, account_id);

CREATE INDEX IF NOT EXISTS idx_gl_journal_details_cost_center
    ON gl_journal_details(cost_center_id);

CREATE INDEX IF NOT EXISTS idx_gl_ledger_scope_period_cost_center
    ON gl_ledger_entries(company_id, location_id, period_month, cost_center_id);

CREATE INDEX IF NOT EXISTS idx_gl_ledger_journal
    ON gl_ledger_entries(journal_id);

ALTER TABLE gl_journal_details
    ADD COLUMN IF NOT EXISTS cost_center_id BIGINT NULL;

ALTER TABLE gl_ledger_entries
    ADD COLUMN IF NOT EXISTS cost_center_id BIGINT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_gl_journal_details_cost_center'
          AND conrelid = 'gl_journal_details'::regclass
    ) THEN
        ALTER TABLE gl_journal_details
            ADD CONSTRAINT fk_gl_journal_details_cost_center
            FOREIGN KEY (cost_center_id) REFERENCES gl_cost_centers(id) ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_gl_ledger_entries_cost_center'
          AND conrelid = 'gl_ledger_entries'::regclass
    ) THEN
        ALTER TABLE gl_ledger_entries
            ADD CONSTRAINT fk_gl_ledger_entries_cost_center
            FOREIGN KEY (cost_center_id) REFERENCES gl_cost_centers(id) ON DELETE RESTRICT;
    END IF;
END
$$;

UPDATE gl_journal_details d
SET cost_center_id = cc.id
FROM gl_journal_headers h,
     gl_cost_centers cc
WHERE d.header_id = h.id
  AND cc.company_id = h.company_id
  AND cc.location_id = h.location_id
  AND upper(cc.cost_center_code) = upper(d.cost_center_code)
  AND d.cost_center_id IS NULL
  AND btrim(coalesce(d.cost_center_code, '')) <> '';

UPDATE gl_ledger_entries le
SET cost_center_id = cc.id
FROM gl_cost_centers cc
WHERE cc.company_id = le.company_id
  AND cc.location_id = le.location_id
  AND upper(cc.cost_center_code) = upper(le.cost_center_code)
  AND le.cost_center_id IS NULL
  AND btrim(coalesce(le.cost_center_code, '')) <> '';

INSERT INTO gl_accounting_periods (company_id, location_id, period_month, is_open, note)
SELECT l.company_id,
       l.id,
       date_trunc('month', CURRENT_DATE)::date,
       TRUE,
       'AUTO_OPENED'
FROM org_locations l
JOIN org_companies c ON c.id = l.company_id
WHERE c.is_active = TRUE
  AND l.is_active = TRUE
ON CONFLICT (company_id, location_id, period_month) DO NOTHING;

INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    is_posting,
    is_active,
    sort_order,
    report_group,
    created_by,
    created_at,
    updated_by,
    updated_at)
WITH location_scope AS (
    SELECT DISTINCT
           c.id AS company_id,
           CASE
               WHEN upper(btrim(l.code)) IN ('HO', 'HQ') THEN 'HO'
               WHEN upper(btrim(l.code)) IN ('PK', 'PKS') THEN 'PK'
               WHEN upper(btrim(l.code)) IN ('KB', 'KEBUN') THEN 'KB'
               ELSE left(upper(btrim(l.code)), 2)
           END AS location_code
    FROM org_companies c
    JOIN org_locations l ON l.company_id = c.id
    WHERE c.is_active = TRUE
      AND l.is_active = TRUE
),
root_templates(account_suffix, account_name, account_type, normal_balance, sort_order, report_group) AS (
    VALUES
        ('10000.000', 'Assets', 'ASSET', 'D', 10, 'BS_ASSET'),
        ('20000.000', 'Liabilities', 'LIABILITY', 'C', 40, 'BS_LIABILITY'),
        ('30000.000', 'Equity', 'EQUITY', 'C', 70, 'BS_EQUITY'),
        ('40000.000', 'Revenue', 'REVENUE', 'C', 80, 'PL_REVENUE'),
        ('50000.000', 'Expenses', 'EXPENSE', 'D', 90, 'PL_EXPENSE')
)
SELECT ls.company_id,
       ls.location_code || '.' || t.account_suffix,
       t.account_name,
       t.account_type,
       t.normal_balance,
       NULL,
       FALSE,
       TRUE,
       t.sort_order,
       t.report_group,
       'SYSTEM',
       NOW(),
       'SYSTEM',
       NOW()
FROM location_scope ls
JOIN root_templates t ON TRUE
WHERE ls.location_code IN ('HO', 'PK', 'KB')
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = NULL,
    is_posting = FALSE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();

INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    is_posting,
    is_active,
    sort_order,
    report_group,
    created_by,
    created_at,
    updated_by,
    updated_at)
WITH location_scope AS (
    SELECT DISTINCT
           c.id AS company_id,
           CASE
               WHEN upper(btrim(l.code)) IN ('HO', 'HQ') THEN 'HO'
               WHEN upper(btrim(l.code)) IN ('PK', 'PKS') THEN 'PK'
               WHEN upper(btrim(l.code)) IN ('KB', 'KEBUN') THEN 'KB'
               ELSE left(upper(btrim(l.code)), 2)
           END AS location_code
    FROM org_companies c
    JOIN org_locations l ON l.company_id = c.id
    WHERE c.is_active = TRUE
      AND l.is_active = TRUE
),
summary_templates(account_suffix, parent_suffix, account_name, account_type, normal_balance, sort_order, report_group) AS (
    VALUES
        ('11100.000', '10000.000', 'Cash and Bank', 'ASSET', 'D', 20, 'BS_ASSET'),
        ('12000.000', '10000.000', 'Trade Receivables', 'ASSET', 'D', 30, 'BS_ASSET'),
        ('21000.000', '20000.000', 'Accounts Payable', 'LIABILITY', 'C', 50, 'BS_LIABILITY'),
        ('33000.000', '30000.000', 'Retained Earnings', 'EQUITY', 'C', 72, 'BS_EQUITY'),
        ('41000.000', '40000.000', 'Operating Revenue', 'REVENUE', 'C', 82, 'PL_REVENUE'),
        ('51000.000', '50000.000', 'Operating Expenses', 'EXPENSE', 'D', 92, 'PL_EXPENSE')
)
SELECT ls.company_id,
       ls.location_code || '.' || t.account_suffix,
       t.account_name,
       t.account_type,
       t.normal_balance,
       parent.id,
       FALSE,
       TRUE,
       t.sort_order,
       t.report_group,
       'SYSTEM',
       NOW(),
       'SYSTEM',
       NOW()
FROM location_scope ls
JOIN summary_templates t ON TRUE
JOIN gl_accounts parent
  ON parent.company_id = ls.company_id
 AND parent.account_code = ls.location_code || '.' || t.parent_suffix
WHERE ls.location_code IN ('HO', 'PK', 'KB')
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = EXCLUDED.parent_account_id,
    is_posting = FALSE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();

INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    is_posting,
    is_active,
    sort_order,
    report_group,
    created_by,
    created_at,
    updated_by,
    updated_at)
WITH location_scope AS (
    SELECT DISTINCT
           c.id AS company_id,
           CASE
               WHEN upper(btrim(l.code)) IN ('HO', 'HQ') THEN 'HO'
               WHEN upper(btrim(l.code)) IN ('PK', 'PKS') THEN 'PK'
               WHEN upper(btrim(l.code)) IN ('KB', 'KEBUN') THEN 'KB'
               ELSE left(upper(btrim(l.code)), 2)
           END AS location_code
    FROM org_companies c
    JOIN org_locations l ON l.company_id = c.id
    WHERE c.is_active = TRUE
      AND l.is_active = TRUE
),
leaf_templates(account_suffix, parent_suffix, account_name, account_type, normal_balance, sort_order, report_group) AS (
    VALUES
        ('11100.001', '11100.000', 'Kas', 'ASSET', 'D', 21, 'BS_ASSET'),
        ('11100.002', '11100.000', 'Bank', 'ASSET', 'D', 22, 'BS_ASSET'),
        ('12000.001', '12000.000', 'Piutang Usaha', 'ASSET', 'D', 31, 'BS_ASSET'),
        ('21000.001', '21000.000', 'Hutang Usaha', 'LIABILITY', 'C', 51, 'BS_LIABILITY'),
        ('30000.001', '30000.000', 'Modal', 'EQUITY', 'C', 71, 'BS_EQUITY'),
        ('33000.001', '33000.000', 'Laba Ditahan', 'EQUITY', 'C', 73, 'BS_EQUITY'),
        ('41000.001', '41000.000', 'Pendapatan', 'REVENUE', 'C', 83, 'PL_REVENUE'),
        ('51000.001', '51000.000', 'Beban Operasional', 'EXPENSE', 'D', 93, 'PL_EXPENSE')
)
SELECT ls.company_id,
       ls.location_code || '.' || t.account_suffix,
       t.account_name,
       t.account_type,
       t.normal_balance,
       parent.id,
       TRUE,
       TRUE,
       t.sort_order,
       t.report_group,
       'SYSTEM',
       NOW(),
       'SYSTEM',
       NOW()
FROM location_scope ls
JOIN leaf_templates t ON TRUE
JOIN gl_accounts parent
  ON parent.company_id = ls.company_id
 AND parent.account_code = ls.location_code || '.' || t.parent_suffix
WHERE ls.location_code IN ('HO', 'PK', 'KB')
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = EXCLUDED.parent_account_id,
    is_posting = TRUE,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();

UPDATE gl_accounts a
SET hierarchy_level = CASE
    WHEN a.parent_account_id IS NULL THEN 1
    WHEN p.parent_account_id IS NULL THEN 2
    ELSE 3
END
FROM gl_accounts p
WHERE a.parent_account_id = p.id
  AND a.hierarchy_level IS DISTINCT FROM CASE
      WHEN p.parent_account_id IS NULL THEN 2
      ELSE 3
  END;

UPDATE gl_accounts
SET hierarchy_level = 1
WHERE parent_account_id IS NULL
  AND hierarchy_level IS DISTINCT FROM 1;

UPDATE gl_accounts a
SET is_posting = NOT EXISTS (
    SELECT 1
    FROM gl_accounts c
    WHERE c.parent_account_id = a.id)
WHERE a.is_posting IS DISTINCT FROM NOT EXISTS (
    SELECT 1
    FROM gl_accounts c
    WHERE c.parent_account_id = a.id);
";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandTimeout = Math.Max(30, _options.QueryTimeoutSeconds)
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SeedPalmOilSampleAccountsAsync(connection, cancellationToken);

            _journalSchemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
