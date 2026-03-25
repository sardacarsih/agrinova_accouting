-- Production-ready GL Account master migration for PostgreSQL.
-- This script upgrades/refines existing gl_accounts in-place (no duplicate account master table).
-- Prerequisite: org_companies table already exists.

BEGIN;

CREATE EXTENSION IF NOT EXISTS ltree;

CREATE TABLE IF NOT EXISTS gl_accounts (
    id BIGSERIAL PRIMARY KEY
);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'gl_accounts'::regclass
          AND attname = 'code'
          AND NOT attisdropped
    ) AND NOT EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'gl_accounts'::regclass
          AND attname = 'account_code'
          AND NOT attisdropped
    ) THEN
        ALTER TABLE gl_accounts RENAME COLUMN code TO account_code;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'gl_accounts'::regclass
          AND attname = 'name'
          AND NOT attisdropped
    ) AND NOT EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'gl_accounts'::regclass
          AND attname = 'account_name'
          AND NOT attisdropped
    ) THEN
        ALTER TABLE gl_accounts RENAME COLUMN name TO account_name;
    END IF;
END
$$;

ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS company_id BIGINT;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS account_code VARCHAR(80);
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS account_name VARCHAR(200);
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS parent_account_id BIGINT;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS account_level INT NOT NULL DEFAULT 1;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS full_path LTREE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS account_type VARCHAR(20) NOT NULL DEFAULT 'ASSET';
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS normal_balance CHAR(1) NOT NULL DEFAULT 'D';
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS is_posting BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS sort_order INT NOT NULL DEFAULT 100;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS report_group VARCHAR(50);
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS cashflow_category VARCHAR(50);
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS allow_manual_journal BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS requires_department BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS requires_project BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS requires_partner BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS is_control_account BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM';
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE gl_accounts ADD COLUMN IF NOT EXISTS updated_by VARCHAR(100);

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
        RAISE EXCEPTION 'org_companies has no rows. Seed company data before running gl_accounts migration.';
    END IF;

    UPDATE gl_accounts
    SET company_id = v_default_company_id
    WHERE company_id IS NULL;
END
$$;

UPDATE gl_accounts
SET account_code = 'ACC' || LPAD(id::text, 6, '0')
WHERE account_code IS NULL OR BTRIM(account_code) = '';

UPDATE gl_accounts
SET account_name = 'Account ' || id::text
WHERE account_name IS NULL OR BTRIM(account_name) = '';

UPDATE gl_accounts
SET account_code = UPPER(BTRIM(account_code)),
    account_name = BTRIM(account_name);

UPDATE gl_accounts
SET account_type = CASE
    WHEN UPPER(BTRIM(account_type)) IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE') THEN UPPER(BTRIM(account_type))
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '1' THEN 'ASSET'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '2' THEN 'LIABILITY'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '3' THEN 'EQUITY'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '4' THEN 'REVENUE'
    WHEN account_code ~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' AND SUBSTRING(account_code FROM 4 FOR 1) = '5' THEN 'EXPENSE'
    WHEN LEFT(account_code, 1) = '1' THEN 'ASSET'
    WHEN LEFT(account_code, 1) = '2' THEN 'LIABILITY'
    WHEN LEFT(account_code, 1) = '3' THEN 'EQUITY'
    WHEN LEFT(account_code, 1) = '4' THEN 'REVENUE'
    WHEN LEFT(account_code, 1) = '5' THEN 'EXPENSE'
    ELSE CASE WHEN UPPER(normal_balance) = 'C' THEN 'LIABILITY' ELSE 'ASSET' END
END
WHERE account_type IS NULL
   OR BTRIM(account_type) = ''
   OR UPPER(BTRIM(account_type)) NOT IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE');

UPDATE gl_accounts
SET normal_balance = CASE
    WHEN UPPER(account_type) IN ('LIABILITY', 'EQUITY', 'REVENUE') THEN 'C'
    ELSE 'D'
END
WHERE normal_balance IS NULL
   OR BTRIM(normal_balance) = ''
   OR UPPER(BTRIM(normal_balance)) NOT IN ('D', 'C');

UPDATE gl_accounts
SET normal_balance = CASE
    WHEN UPPER(account_type) IN ('LIABILITY', 'EQUITY', 'REVENUE') THEN 'C'
    ELSE 'D'
END
WHERE (
        UPPER(account_type) IN ('ASSET', 'EXPENSE')
        AND normal_balance <> 'D'
      )
   OR (
        UPPER(account_type) IN ('LIABILITY', 'EQUITY', 'REVENUE')
        AND normal_balance <> 'C'
      );

UPDATE gl_accounts
SET created_by = 'SYSTEM'
WHERE created_by IS NULL OR BTRIM(created_by) = '';

UPDATE gl_accounts p
SET is_posting = FALSE
WHERE EXISTS (
    SELECT 1
    FROM gl_accounts c
    WHERE c.parent_account_id = p.id
);

ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS gl_accounts_code_key;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_type;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_normal_balance;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_type_balance_consistency;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_code_not_blank;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_name_not_blank;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_level_positive;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS chk_gl_accounts_parent_not_self;
ALTER TABLE gl_accounts DROP CONSTRAINT IF EXISTS gl_accounts_parent_account_id_fkey;

ALTER TABLE gl_accounts
    ALTER COLUMN company_id SET NOT NULL,
    ALTER COLUMN account_code SET NOT NULL,
    ALTER COLUMN account_name SET NOT NULL,
    ALTER COLUMN account_level SET NOT NULL,
    ALTER COLUMN account_type SET NOT NULL,
    ALTER COLUMN normal_balance SET NOT NULL,
    ALTER COLUMN is_posting SET NOT NULL,
    ALTER COLUMN is_active SET NOT NULL,
    ALTER COLUMN sort_order SET NOT NULL,
    ALTER COLUMN created_at SET NOT NULL,
    ALTER COLUMN created_by SET NOT NULL,
    ALTER COLUMN updated_at SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_gl_accounts_company'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT fk_gl_accounts_company
            FOREIGN KEY (company_id) REFERENCES org_companies(id) ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_accounts_company_code'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT uq_gl_accounts_company_code
            UNIQUE (company_id, account_code);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_gl_accounts_company_id_id'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT uq_gl_accounts_company_id_id
            UNIQUE (company_id, id);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_gl_accounts_parent_same_company'
          AND conrelid = 'gl_accounts'::regclass
    ) THEN
        ALTER TABLE gl_accounts
            ADD CONSTRAINT fk_gl_accounts_parent_same_company
            FOREIGN KEY (company_id, parent_account_id)
            REFERENCES gl_accounts(company_id, id)
            ON DELETE RESTRICT
            DEFERRABLE INITIALLY IMMEDIATE;
    END IF;
END
$$;

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_code_not_blank
    CHECK (BTRIM(account_code) <> '');

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_name_not_blank
    CHECK (BTRIM(account_name) <> '');

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_level_positive
    CHECK (account_level >= 1);

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_type
    CHECK (account_type IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE'));

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_normal_balance
    CHECK (normal_balance IN ('D', 'C'));

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_parent_not_self
    CHECK (parent_account_id IS NULL OR parent_account_id <> id);

ALTER TABLE gl_accounts
    ADD CONSTRAINT chk_gl_accounts_type_balance_consistency
    CHECK (
        (account_type IN ('ASSET', 'EXPENSE') AND normal_balance = 'D')
        OR
        (account_type IN ('LIABILITY', 'EQUITY', 'REVENUE') AND normal_balance = 'C')
    );

CREATE OR REPLACE FUNCTION gl_accounts_normalize_label(p_account_code TEXT)
RETURNS TEXT
LANGUAGE SQL
IMMUTABLE
AS $$
SELECT REGEXP_REPLACE(UPPER(BTRIM(COALESCE(p_account_code, ''))), '[^A-Z0-9_]', '_', 'g');
$$;

CREATE OR REPLACE FUNCTION gl_accounts_biu_set_hierarchy()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_parent_level INT;
    v_parent_path LTREE;
    v_parent_is_posting BOOLEAN;
    v_cycle_exists BOOLEAN;
    v_label TEXT;
BEGIN
    NEW.account_code := UPPER(BTRIM(NEW.account_code));
    NEW.account_name := BTRIM(NEW.account_name);
    NEW.account_type := UPPER(BTRIM(NEW.account_type));
    NEW.normal_balance := UPPER(BTRIM(NEW.normal_balance));
    NEW.updated_at := NOW();

    IF NEW.account_type NOT IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE') THEN
        RAISE EXCEPTION 'Invalid account_type: %', NEW.account_type;
    END IF;

    IF NEW.normal_balance NOT IN ('D', 'C') THEN
        RAISE EXCEPTION 'Invalid normal_balance: %', NEW.normal_balance;
    END IF;

    IF NEW.account_code !~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' THEN
        RAISE EXCEPTION 'Invalid account_code format %. Expected XX.XXXXX.XXX.', NEW.account_code;
    END IF;

    IF (NEW.account_type IN ('ASSET', 'EXPENSE') AND NEW.normal_balance <> 'D')
       OR (NEW.account_type IN ('LIABILITY', 'EQUITY', 'REVENUE') AND NEW.normal_balance <> 'C') THEN
        RAISE EXCEPTION 'account_type % must use normal_balance %',
            NEW.account_type,
            CASE WHEN NEW.account_type IN ('ASSET', 'EXPENSE') THEN 'D' ELSE 'C' END;
    END IF;

    v_label := gl_accounts_normalize_label(NEW.account_code);
    IF v_label = '' THEN
        RAISE EXCEPTION 'account_code is not valid for hierarchy path.';
    END IF;

    IF NEW.parent_account_id IS NULL THEN
        NEW.account_level := 1;
        NEW.full_path := TEXT2LTREE(v_label);
    ELSE
        SELECT account_level, full_path, is_posting
        INTO v_parent_level, v_parent_path, v_parent_is_posting
        FROM gl_accounts
        WHERE id = NEW.parent_account_id
          AND company_id = NEW.company_id;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Parent account % not found in company %', NEW.parent_account_id, NEW.company_id;
        END IF;

        IF v_parent_is_posting THEN
            RAISE EXCEPTION 'Parent account % is posting and cannot have children.', NEW.parent_account_id;
        END IF;

        IF NEW.id IS NOT NULL THEN
            WITH RECURSIVE parents AS (
                SELECT id, parent_account_id
                FROM gl_accounts
                WHERE id = NEW.parent_account_id
                  AND company_id = NEW.company_id
                UNION ALL
                SELECT g.id, g.parent_account_id
                FROM gl_accounts g
                JOIN parents p ON p.parent_account_id = g.id
                WHERE g.company_id = NEW.company_id
            )
            SELECT EXISTS (
                SELECT 1
                FROM parents
                WHERE id = NEW.id
            )
            INTO v_cycle_exists;

            IF v_cycle_exists THEN
                RAISE EXCEPTION 'Hierarchy cycle detected for account id %', NEW.id;
            END IF;
        END IF;

        NEW.account_level := v_parent_level + 1;
        NEW.full_path := v_parent_path || TEXT2LTREE(v_label);
    END IF;

    IF NEW.id IS NOT NULL
       AND NEW.is_posting
       AND EXISTS (
           SELECT 1
           FROM gl_accounts c
           WHERE c.parent_account_id = NEW.id
       ) THEN
        RAISE EXCEPTION 'Posting account % cannot have child accounts.', NEW.id;
    END IF;

    RETURN NEW;
END
$$;

CREATE OR REPLACE FUNCTION gl_accounts_au_propagate_path()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF OLD.full_path IS DISTINCT FROM NEW.full_path THEN
        UPDATE gl_accounts c
        SET full_path = NEW.full_path || SUBPATH(c.full_path, NLEVEL(OLD.full_path)),
            account_level = NLEVEL(NEW.full_path || SUBPATH(c.full_path, NLEVEL(OLD.full_path))),
            updated_at = NOW()
        WHERE c.company_id = NEW.company_id
          AND c.id <> NEW.id
          AND c.full_path <@ OLD.full_path;
    END IF;

    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS trg_gl_accounts_biu_set_hierarchy ON gl_accounts;
CREATE TRIGGER trg_gl_accounts_biu_set_hierarchy
BEFORE INSERT OR UPDATE OF company_id, parent_account_id, account_code, account_name, account_type, normal_balance, is_posting
ON gl_accounts
FOR EACH ROW
EXECUTE FUNCTION gl_accounts_biu_set_hierarchy();

DROP TRIGGER IF EXISTS trg_gl_accounts_au_propagate_path ON gl_accounts;
CREATE TRIGGER trg_gl_accounts_au_propagate_path
AFTER UPDATE OF full_path
ON gl_accounts
FOR EACH ROW
EXECUTE FUNCTION gl_accounts_au_propagate_path();

WITH RECURSIVE account_tree AS (
    SELECT a.id,
           a.company_id,
           1 AS account_level,
           TEXT2LTREE(gl_accounts_normalize_label(a.account_code)) AS full_path,
           ARRAY[a.id] AS visited_ids
    FROM gl_accounts a
    WHERE a.parent_account_id IS NULL
    UNION ALL
    SELECT c.id,
           c.company_id,
           p.account_level + 1 AS account_level,
           p.full_path || TEXT2LTREE(gl_accounts_normalize_label(c.account_code)) AS full_path,
           p.visited_ids || c.id AS visited_ids
    FROM gl_accounts c
    JOIN account_tree p
      ON p.id = c.parent_account_id
     AND p.company_id = c.company_id
    WHERE NOT (c.id = ANY(p.visited_ids))
)
UPDATE gl_accounts g
SET account_level = t.account_level,
    full_path = t.full_path,
    updated_at = NOW()
FROM account_tree t
WHERE g.id = t.id;

DO $$
DECLARE
    v_missing_path INT;
BEGIN
    SELECT COUNT(1)
    INTO v_missing_path
    FROM gl_accounts
    WHERE full_path IS NULL;

    IF v_missing_path > 0 THEN
        RAISE EXCEPTION 'Hierarchy rebuild failed. Found % account(s) with NULL full_path (possible orphan/cycle).', v_missing_path;
    END IF;
END
$$;

ALTER TABLE gl_accounts
    ALTER COLUMN full_path SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_id
    ON gl_accounts(company_id);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_parent_account_id
    ON gl_accounts(parent_account_id);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_parent_sort
    ON gl_accounts(company_id, parent_account_id, sort_order, account_code);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_type_active
    ON gl_accounts(company_id, account_type, is_active);

CREATE INDEX IF NOT EXISTS idx_gl_accounts_company_posting_active
    ON gl_accounts(company_id, account_code)
    WHERE is_posting = TRUE AND is_active = TRUE;

CREATE INDEX IF NOT EXISTS idx_gl_accounts_full_path_gist
    ON gl_accounts USING GIST(full_path);

CREATE OR REPLACE FUNCTION gl_journal_details_biu_validate_account()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_header_company_id BIGINT;
    v_account_company_id BIGINT;
    v_is_posting BOOLEAN;
    v_is_active BOOLEAN;
BEGIN
    SELECT h.company_id
    INTO v_header_company_id
    FROM gl_journal_headers h
    WHERE h.id = NEW.header_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal header % not found.', NEW.header_id;
    END IF;

    SELECT a.company_id, a.is_posting, a.is_active
    INTO v_account_company_id, v_is_posting, v_is_active
    FROM gl_accounts a
    WHERE a.id = NEW.account_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Account % not found.', NEW.account_id;
    END IF;

    IF v_account_company_id <> v_header_company_id THEN
        RAISE EXCEPTION 'Account company % differs from journal company %.', v_account_company_id, v_header_company_id;
    END IF;

    IF NOT v_is_posting THEN
        RAISE EXCEPTION 'Account % is non-posting and cannot be used in journal details.', NEW.account_id;
    END IF;

    IF NOT v_is_active THEN
        RAISE EXCEPTION 'Account % is inactive and cannot be used in journal details.', NEW.account_id;
    END IF;

    RETURN NEW;
END
$$;

DO $$
BEGIN
    IF TO_REGCLASS('gl_journal_details') IS NOT NULL
       AND TO_REGCLASS('gl_journal_headers') IS NOT NULL THEN
        EXECUTE 'DROP TRIGGER IF EXISTS trg_gl_journal_details_biu_validate_account ON gl_journal_details';
        EXECUTE 'CREATE TRIGGER trg_gl_journal_details_biu_validate_account
                 BEFORE INSERT OR UPDATE OF account_id, header_id
                 ON gl_journal_details
                 FOR EACH ROW
                 EXECUTE FUNCTION gl_journal_details_biu_validate_account()';
    END IF;
END
$$;

DO $$
DECLARE
    v_company_id BIGINT;
    v_location_code TEXT;
    v_prefix TEXT;
BEGIN
    SELECT id
    INTO v_company_id
    FROM org_companies
    WHERE code = 'AGRINOVA'
    ORDER BY id
    LIMIT 1;

    IF v_company_id IS NULL THEN
        RETURN;
    END IF;

    FOREACH v_location_code IN ARRAY ARRAY['HO', 'PK', 'KB']
    LOOP
        v_prefix := v_location_code || '.';

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        VALUES
            (v_company_id, v_prefix || '10000.000', 'Assets', NULL, 'ASSET', 'D', FALSE, TRUE, 10, 'BS_ASSET', FALSE, 'SEED', 'SEED'),
            (v_company_id, v_prefix || '20000.000', 'Liabilities', NULL, 'LIABILITY', 'C', FALSE, TRUE, 50, 'BS_LIABILITY', FALSE, 'SEED', 'SEED'),
            (v_company_id, v_prefix || '30000.000', 'Equity', NULL, 'EQUITY', 'C', FALSE, TRUE, 70, 'BS_EQUITY', FALSE, 'SEED', 'SEED'),
            (v_company_id, v_prefix || '40000.000', 'Revenue', NULL, 'REVENUE', 'C', FALSE, TRUE, 80, 'PL_REVENUE', FALSE, 'SEED', 'SEED'),
            (v_company_id, v_prefix || '50000.000', 'Expenses', NULL, 'EXPENSE', 'D', FALSE, TRUE, 90, 'PL_EXPENSE', FALSE, 'SEED', 'SEED')
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '11000.000', 'Current Assets', p.id, 'ASSET', 'D', FALSE, TRUE, 20, 'BS_ASSET', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '10000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '11100.000', 'Cash and Bank', p.id, 'ASSET', 'D', FALSE, TRUE, 30, 'BS_ASSET', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '11000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '11100.001', 'Cash on Hand', p.id, 'ASSET', 'D', TRUE, TRUE, 31, 'BS_ASSET', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '11100.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '11100.002', 'Bank Account', p.id, 'ASSET', 'D', TRUE, TRUE, 32, 'BS_ASSET', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '11100.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '21000.000', 'Current Liabilities', p.id, 'LIABILITY', 'C', FALSE, TRUE, 60, 'BS_LIABILITY', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '20000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '21000.001', 'Accounts Payable', p.id, 'LIABILITY', 'C', TRUE, TRUE, 61, 'BS_LIABILITY', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '21000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '33000.000', 'Retained Earnings', p.id, 'EQUITY', 'C', FALSE, TRUE, 71, 'BS_EQUITY', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '30000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '33000.001', 'Laba Ditahan', p.id, 'EQUITY', 'C', TRUE, TRUE, 72, 'BS_EQUITY', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '33000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '41000.000', 'Operating Revenue', p.id, 'REVENUE', 'C', FALSE, TRUE, 81, 'PL_REVENUE', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '40000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '41000.001', 'Sales Revenue', p.id, 'REVENUE', 'C', TRUE, TRUE, 82, 'PL_REVENUE', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '41000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '51000.000', 'Operating Expenses', p.id, 'EXPENSE', 'D', FALSE, TRUE, 91, 'PL_EXPENSE', FALSE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '50000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();

        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, sort_order, report_group, allow_manual_journal, created_by, updated_by
        )
        SELECT v_company_id, v_prefix || '51000.001', 'Beban Operasional', p.id, 'EXPENSE', 'D', TRUE, TRUE, 92, 'PL_EXPENSE', TRUE, 'SEED', 'SEED'
        FROM gl_accounts p
        WHERE p.company_id = v_company_id
          AND p.account_code = v_prefix || '51000.000'
        ON CONFLICT (company_id, account_code) DO UPDATE
        SET account_name = EXCLUDED.account_name,
            parent_account_id = EXCLUDED.parent_account_id,
            account_type = EXCLUDED.account_type,
            normal_balance = EXCLUDED.normal_balance,
            is_posting = EXCLUDED.is_posting,
            is_active = EXCLUDED.is_active,
            sort_order = EXCLUDED.sort_order,
            report_group = EXCLUDED.report_group,
            allow_manual_journal = EXCLUDED.allow_manual_journal,
            updated_by = EXCLUDED.updated_by,
            updated_at = NOW();
    END LOOP;
END
$$;

COMMIT;
