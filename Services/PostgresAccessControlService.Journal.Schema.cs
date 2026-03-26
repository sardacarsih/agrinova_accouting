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
    ELSE 2
END
WHERE hierarchy_level IS DISTINCT FROM CASE
    WHEN parent_account_id IS NULL THEN 1
    ELSE 2
END;

UPDATE gl_accounts
SET is_posting = CASE
    WHEN parent_account_id IS NULL THEN FALSE
    ELSE TRUE
END
WHERE is_posting IS DISTINCT FROM CASE
    WHEN parent_account_id IS NULL THEN FALSE
    ELSE TRUE
END;

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
    CONSTRAINT uq_gl_journal_company_location_no UNIQUE (company_id, location_id, journal_no),
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

CREATE INDEX IF NOT EXISTS idx_gl_accounting_period_scope
    ON gl_accounting_periods(company_id, location_id, period_month);

CREATE INDEX IF NOT EXISTS idx_gl_ledger_scope_period_account
    ON gl_ledger_entries(company_id, location_id, period_month, account_id);

CREATE INDEX IF NOT EXISTS idx_gl_ledger_journal
    ON gl_ledger_entries(journal_id);

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
    is_posting,
    is_active,
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
account_templates(account_suffix, account_name, account_type, normal_balance) AS (
    VALUES
        ('11100.001', 'Kas', 'ASSET', 'D'),
        ('11100.002', 'Bank', 'ASSET', 'D'),
        ('12000.001', 'Piutang Usaha', 'ASSET', 'D'),
        ('21000.001', 'Hutang Usaha', 'LIABILITY', 'C'),
        ('30000.001', 'Modal', 'EQUITY', 'C'),
        ('33000.001', 'Laba Ditahan', 'EQUITY', 'C'),
        ('41000.001', 'Pendapatan', 'REVENUE', 'C'),
        ('51000.001', 'Beban Operasional', 'EXPENSE', 'D')
)
SELECT ls.company_id,
       ls.location_code || '.' || v.account_suffix,
       v.account_name,
       v.account_type,
       v.normal_balance,
       TRUE,
       TRUE,
       'SYSTEM',
       NOW(),
       'SYSTEM',
       NOW()
FROM location_scope ls
JOIN account_templates v ON TRUE
WHERE ls.location_code IN ('HO', 'PK', 'KB')
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    updated_by = EXCLUDED.updated_by,
    updated_at = NOW();
";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection)
            {
                CommandTimeout = Math.Max(30, _options.QueryTimeoutSeconds)
            };
            await command.ExecuteNonQueryAsync(cancellationToken);

            _journalSchemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
