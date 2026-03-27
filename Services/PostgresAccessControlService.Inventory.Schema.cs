using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private async Task EnsureInventorySchemaAsync(CancellationToken cancellationToken)
    {
        if (_inventorySchemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_inventorySchemaEnsured)
            {
                return;
            }

            const string sql = @"
CREATE TABLE IF NOT EXISTS app_system_settings (
    setting_key VARCHAR(120) PRIMARY KEY,
    setting_value TEXT NOT NULL DEFAULT '',
    updated_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inv_sync_runs (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    direction VARCHAR(20) NOT NULL CHECK (direction IN ('UPLOAD','DOWNLOAD')),
    trigger_mode VARCHAR(20) NOT NULL DEFAULT 'MANUAL',
    status VARCHAR(20) NOT NULL DEFAULT 'RUNNING' CHECK (status IN ('RUNNING','SUCCESS','PARTIAL','FAILED')),
    actor_username VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMPTZ,
    watermark_from_utc TIMESTAMPTZ,
    watermark_to_utc TIMESTAMPTZ,
    total_items INT NOT NULL DEFAULT 0,
    success_items INT NOT NULL DEFAULT 0,
    failed_items INT NOT NULL DEFAULT 0,
    message TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS inv_sync_item_logs (
    id BIGSERIAL PRIMARY KEY,
    sync_run_id BIGINT NOT NULL REFERENCES inv_sync_runs(id) ON DELETE CASCADE,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    direction VARCHAR(20) NOT NULL CHECK (direction IN ('UPLOAD','DOWNLOAD')),
    item_code VARCHAR(80) NOT NULL,
    category_code VARCHAR(80) NOT NULL DEFAULT '',
    operation VARCHAR(30) NOT NULL DEFAULT 'UPSERT',
    result VARCHAR(20) NOT NULL DEFAULT 'SUCCESS' CHECK (result IN ('SUCCESS','FAILED','SKIPPED')),
    error_message TEXT NOT NULL DEFAULT '',
    logged_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_inv_sync_runs_company_started
    ON inv_sync_runs(company_id, started_at DESC);

CREATE INDEX IF NOT EXISTS idx_inv_sync_item_logs_run
    ON inv_sync_item_logs(sync_run_id, logged_at DESC);

CREATE TABLE IF NOT EXISTS inv_sync_watermarks (
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    direction VARCHAR(20) NOT NULL CHECK (direction IN ('UPLOAD','DOWNLOAD')),
    last_success_utc TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (company_id, direction)
);

CREATE TABLE IF NOT EXISTS inv_categories (
    id BIGSERIAL PRIMARY KEY,
    category_code VARCHAR(80) NOT NULL,
    category_name VARCHAR(200) NOT NULL,
    account_code VARCHAR(80) NOT NULL DEFAULT '',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_categories_code UNIQUE (category_code)
);

CREATE TABLE IF NOT EXISTS inv_items (
    id BIGSERIAL PRIMARY KEY,
    item_code VARCHAR(80) NOT NULL,
    item_name VARCHAR(200) NOT NULL,
    uom VARCHAR(20) NOT NULL DEFAULT 'PCS',
    category VARCHAR(80) NOT NULL DEFAULT '',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_items_code UNIQUE (item_code)
);

ALTER TABLE inv_items ADD COLUMN IF NOT EXISTS category_id BIGINT REFERENCES inv_categories(id) ON DELETE SET NULL;
ALTER TABLE inv_items DROP COLUMN IF EXISTS account_code;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'inv_categories'
          AND column_name = 'company_id'
    ) THEN
        CREATE TEMP TABLE tmp_inv_category_canonical ON COMMIT DROP AS
        SELECT DISTINCT ON (COALESCE(NULLIF(upper(btrim(category_code)), ''), '#EMPTY#'))
               id AS canonical_category_id,
               COALESCE(NULLIF(upper(btrim(category_code)), ''), '#EMPTY#') AS category_code_key
        FROM inv_categories
        ORDER BY COALESCE(NULLIF(upper(btrim(category_code)), ''), '#EMPTY#'),
                 COALESCE(updated_at, created_at, NOW()) DESC,
                 id DESC;

        UPDATE inv_items i
        SET category_id = map.canonical_category_id
        FROM inv_categories src
        JOIN tmp_inv_category_canonical map
          ON map.category_code_key = COALESCE(NULLIF(upper(btrim(src.category_code)), ''), '#EMPTY#')
        WHERE i.category_id = src.id
          AND src.id <> map.canonical_category_id;

        DELETE FROM inv_categories src
        USING tmp_inv_category_canonical map
        WHERE COALESCE(NULLIF(upper(btrim(src.category_code)), ''), '#EMPTY#') = map.category_code_key
          AND src.id <> map.canonical_category_id;

        UPDATE inv_categories
        SET category_code = upper(btrim(COALESCE(category_code, ''))),
            category_name = btrim(COALESCE(category_name, '')),
            account_code = upper(btrim(COALESCE(account_code, ''))),
            updated_at = NOW();

        ALTER TABLE inv_categories DROP CONSTRAINT IF EXISTS uq_inv_categories_company_code;
        ALTER TABLE inv_categories DROP COLUMN IF EXISTS company_id;

        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint
            WHERE conname = 'uq_inv_categories_code'
              AND conrelid = 'inv_categories'::regclass
        ) THEN
            ALTER TABLE inv_categories
                ADD CONSTRAINT uq_inv_categories_code UNIQUE (category_code);
        END IF;
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'inv_items'
          AND column_name = 'company_id'
    ) THEN
        CREATE TEMP TABLE tmp_inv_item_canonical ON COMMIT DROP AS
        SELECT DISTINCT ON (COALESCE(NULLIF(upper(btrim(item_code)), ''), '#EMPTY#'))
               id AS canonical_item_id,
               COALESCE(NULLIF(upper(btrim(item_code)), ''), '#EMPTY#') AS item_code_key
        FROM inv_items
        ORDER BY COALESCE(NULLIF(upper(btrim(item_code)), ''), '#EMPTY#'),
                 COALESCE(updated_at, created_at, NOW()) DESC,
                 id DESC;

        CREATE TEMP TABLE tmp_inv_item_remap ON COMMIT DROP AS
        SELECT src.id AS source_item_id,
               map.canonical_item_id
        FROM inv_items src
        JOIN tmp_inv_item_canonical map
          ON map.item_code_key = COALESCE(NULLIF(upper(btrim(src.item_code)), ''), '#EMPTY#')
        WHERE src.id <> map.canonical_item_id;

        INSERT INTO inv_stock (company_id, location_id, item_id, qty, warehouse_id, updated_at)
        SELECT s.company_id,
               s.location_id,
               r.canonical_item_id,
               SUM(s.qty) AS qty,
               s.warehouse_id AS warehouse_id,
               NOW()
        FROM inv_stock s
        JOIN tmp_inv_item_remap r ON r.source_item_id = s.item_id
        GROUP BY s.company_id, s.location_id, r.canonical_item_id, s.warehouse_id
        ON CONFLICT (company_id, location_id, item_id, warehouse_id) DO UPDATE
        SET qty = inv_stock.qty + EXCLUDED.qty,
            updated_at = NOW();

        DELETE FROM inv_stock s
        USING tmp_inv_item_remap r
        WHERE s.item_id = r.source_item_id;

        UPDATE inv_stock_transaction_lines l
        SET item_id = r.canonical_item_id
        FROM tmp_inv_item_remap r
        WHERE l.item_id = r.source_item_id;

        UPDATE inv_stock_opname_lines l
        SET item_id = r.canonical_item_id
        FROM tmp_inv_item_remap r
        WHERE l.item_id = r.source_item_id;

        UPDATE inv_cost_layers l
        SET item_id = r.canonical_item_id
        FROM tmp_inv_item_remap r
        WHERE l.item_id = r.source_item_id;

        UPDATE inv_cost_outbound_events e
        SET item_id = r.canonical_item_id
        FROM tmp_inv_item_remap r
        WHERE e.item_id = r.source_item_id;

        DELETE FROM inv_items i
        USING tmp_inv_item_remap r
        WHERE i.id = r.source_item_id;

        UPDATE inv_items i
        SET item_code = upper(btrim(COALESCE(i.item_code, ''))),
            item_name = btrim(COALESCE(i.item_name, '')),
            uom = CASE
                WHEN btrim(COALESCE(i.uom, '')) = '' THEN 'PCS'
                ELSE upper(btrim(i.uom))
            END,
            category = btrim(COALESCE(i.category, '')),
            updated_at = NOW();

        ALTER TABLE inv_items DROP CONSTRAINT IF EXISTS uq_inv_items_company_code;
        ALTER TABLE inv_items DROP COLUMN IF EXISTS company_id;

        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint
            WHERE conname = 'uq_inv_items_code'
              AND conrelid = 'inv_items'::regclass
        ) THEN
            ALTER TABLE inv_items
                ADD CONSTRAINT uq_inv_items_code UNIQUE (item_code);
        END IF;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS inv_company_settings (
    company_id BIGINT PRIMARY KEY REFERENCES org_companies(id) ON DELETE CASCADE,
    valuation_method VARCHAR(20) NOT NULL DEFAULT 'AVERAGE' CHECK (valuation_method IN ('FIFO','LIFO','AVERAGE')),
    cogs_account_code VARCHAR(80) NOT NULL DEFAULT '',
    updated_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inv_location_costing_settings (
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    valuation_method VARCHAR(20) NOT NULL DEFAULT 'AVERAGE' CHECK (valuation_method IN ('FIFO','LIFO','AVERAGE')),
    cogs_account_code VARCHAR(80) NOT NULL DEFAULT '',
    updated_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (company_id, location_id)
);

CREATE TABLE IF NOT EXISTS inv_cost_recalc_runs (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    scope VARCHAR(20) NOT NULL CHECK (scope IN ('COMPANY','LOCATION')),
    status VARCHAR(20) NOT NULL CHECK (status IN ('RUNNING','SUCCESS','FAILED')),
    actor_username VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMPTZ NULL,
    affected_location_count INT NOT NULL DEFAULT 0,
    adjustment_journal_count INT NOT NULL DEFAULT 0,
    adjustment_journal_nos TEXT NOT NULL DEFAULT '',
    message TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_inv_cost_recalc_runs_company_started
    ON inv_cost_recalc_runs(company_id, started_at DESC);

CREATE TABLE IF NOT EXISTS inv_stock (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE RESTRICT,
    warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE SET NULL,
    qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_stock_item_location_warehouse UNIQUE (company_id, location_id, item_id, warehouse_id)
);

CREATE TABLE IF NOT EXISTS inv_units (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    unit_code VARCHAR(20) NOT NULL,
    unit_name VARCHAR(100) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_units_company_code UNIQUE (company_id, unit_code)
);

CREATE TABLE IF NOT EXISTS inv_warehouses (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    warehouse_code VARCHAR(80) NOT NULL,
    warehouse_name VARCHAR(200) NOT NULL,
    location_id BIGINT REFERENCES org_locations(id) ON DELETE SET NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_warehouses_company_code UNIQUE (company_id, warehouse_code)
);

CREATE TABLE IF NOT EXISTS inv_stock_transactions (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    transaction_no VARCHAR(80) NOT NULL,
    transaction_type VARCHAR(20) NOT NULL CHECK (transaction_type IN ('STOCK_IN','STOCK_OUT','TRANSFER')),
    transaction_date DATE NOT NULL DEFAULT CURRENT_DATE,
    warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT,
    destination_warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT,
    reference_no VARCHAR(200) NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','SUBMITTED','APPROVED','POSTED')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_stock_transactions_company_no UNIQUE (company_id, transaction_no)
);

CREATE TABLE IF NOT EXISTS inv_stock_transaction_lines (
    id BIGSERIAL PRIMARY KEY,
    transaction_id BIGINT NOT NULL REFERENCES inv_stock_transactions(id) ON DELETE CASCADE,
    line_no INT NOT NULL DEFAULT 1,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE RESTRICT,
    qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost NUMERIC(18,4) NOT NULL DEFAULT 0,
    warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT,
    destination_warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT,
    expense_account_code VARCHAR(80) NOT NULL DEFAULT '',
    notes TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inv_stock_opname (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    opname_no VARCHAR(80) NOT NULL,
    opname_date DATE NOT NULL DEFAULT CURRENT_DATE,
    warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT,
    description TEXT NOT NULL DEFAULT '',
    status VARCHAR(20) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','SUBMITTED','APPROVED','POSTED')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    updated_by VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_stock_opname_company_no UNIQUE (company_id, opname_no)
);

CREATE TABLE IF NOT EXISTS inv_stock_opname_lines (
    id BIGSERIAL PRIMARY KEY,
    opname_id BIGINT NOT NULL REFERENCES inv_stock_opname(id) ON DELETE CASCADE,
    line_no INT NOT NULL DEFAULT 1,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE RESTRICT,
    system_qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    actual_qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    difference_qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    notes TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inv_cost_layers (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE CASCADE,
    source_type VARCHAR(30) NOT NULL,
    source_id BIGINT NOT NULL,
    source_line_id BIGINT NOT NULL,
    layer_date DATE NOT NULL,
    remaining_qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost NUMERIC(18,4) NOT NULL DEFAULT 0,
    created_by VARCHAR(100) NOT NULL DEFAULT 'SYSTEM',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_inv_cost_layers_lookup
    ON inv_cost_layers(company_id, location_id, item_id, layer_date, id);

CREATE INDEX IF NOT EXISTS idx_inv_cost_layers_company
    ON inv_cost_layers(company_id);

CREATE TABLE IF NOT EXISTS inv_cost_outbound_events (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE CASCADE,
    source_type VARCHAR(30) NOT NULL,
    source_id BIGINT NOT NULL,
    source_line_id BIGINT NOT NULL,
    event_date DATE NOT NULL,
    qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    unit_cost NUMERIC(18,4) NOT NULL DEFAULT 0,
    total_cost NUMERIC(18,4) NOT NULL DEFAULT 0,
    valuation_method VARCHAR(20) NOT NULL DEFAULT 'AVERAGE',
    inventory_account_code VARCHAR(80) NOT NULL DEFAULT '',
    cogs_account_code VARCHAR(80) NOT NULL DEFAULT '',
    cogs_journal_id BIGINT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_cost_outbound_source UNIQUE (source_type, source_line_id)
);

CREATE INDEX IF NOT EXISTS idx_inv_cost_outbound_company_date
    ON inv_cost_outbound_events(company_id, event_date, id);

CREATE TABLE IF NOT EXISTS inv_cost_adjustment_events (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE CASCADE,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE CASCADE,
    recalc_run_id BIGINT NOT NULL REFERENCES inv_cost_recalc_runs(id) ON DELETE CASCADE,
    source_ref_no VARCHAR(100) NOT NULL,
    event_date DATE NOT NULL,
    valuation_method VARCHAR(20) NOT NULL DEFAULT 'AVERAGE',
    inventory_account_code VARCHAR(80) NOT NULL DEFAULT '',
    cogs_account_code VARCHAR(80) NOT NULL DEFAULT '',
    delta_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
    cogs_journal_id BIGINT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_cost_adjustment_event UNIQUE (recalc_run_id, location_id, inventory_account_code)
);

CREATE INDEX IF NOT EXISTS idx_inv_cost_adjustment_company_date
    ON inv_cost_adjustment_events(company_id, event_date, id);

ALTER TABLE inv_stock ADD COLUMN IF NOT EXISTS warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE SET NULL;
ALTER TABLE inv_stock_transactions ADD COLUMN IF NOT EXISTS cogs_journal_id BIGINT NULL;
ALTER TABLE inv_stock_opname ADD COLUMN IF NOT EXISTS cogs_journal_id BIGINT NULL;
ALTER TABLE inv_stock_transaction_lines ADD COLUMN IF NOT EXISTS expense_account_code VARCHAR(80) NOT NULL DEFAULT '';
ALTER TABLE inv_stock_transaction_lines ADD COLUMN IF NOT EXISTS warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT;
ALTER TABLE inv_stock_transaction_lines ADD COLUMN IF NOT EXISTS destination_warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE RESTRICT;
ALTER TABLE inv_cost_outbound_events ADD COLUMN IF NOT EXISTS cogs_account_code VARCHAR(80) NOT NULL DEFAULT '';

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.table_constraints
        WHERE table_schema = 'public'
          AND table_name = 'inv_stock'
          AND constraint_name = 'uq_inv_stock_item_location'
    ) THEN
        ALTER TABLE inv_stock DROP CONSTRAINT uq_inv_stock_item_location;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.table_constraints
        WHERE table_schema = 'public'
          AND table_name = 'inv_stock'
          AND constraint_name = 'uq_inv_stock_item_location_warehouse'
    ) THEN
        ALTER TABLE inv_stock
            ADD CONSTRAINT uq_inv_stock_item_location_warehouse
            UNIQUE (company_id, location_id, item_id, warehouse_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_inv_stock_legacy_null_warehouse
    ON inv_stock(company_id, location_id, item_id)
    WHERE warehouse_id IS NULL AND qty > 0;

UPDATE inv_stock_transaction_lines l
SET warehouse_id = h.warehouse_id,
    destination_warehouse_id = h.destination_warehouse_id
FROM inv_stock_transactions h
WHERE l.transaction_id = h.id
  AND l.warehouse_id IS NULL
  AND h.warehouse_id IS NOT NULL;

UPDATE inv_stock_transaction_lines l
SET destination_warehouse_id = h.destination_warehouse_id
FROM inv_stock_transactions h
WHERE l.transaction_id = h.id
  AND l.destination_warehouse_id IS NULL
  AND h.destination_warehouse_id IS NOT NULL;

UPDATE inv_stock_transaction_lines l
SET expense_account_code = upper(btrim(COALESCE(NULLIF(loc.cogs_account_code, ''), NULLIF(comp.cogs_account_code, ''), '')))
FROM inv_stock_transactions h
LEFT JOIN inv_location_costing_settings loc
    ON loc.company_id = h.company_id
   AND loc.location_id = h.location_id
LEFT JOIN inv_company_settings comp
    ON comp.company_id = h.company_id
WHERE l.transaction_id = h.id
  AND (l.expense_account_code IS NULL OR btrim(l.expense_account_code) = '');

UPDATE inv_cost_outbound_events e
SET cogs_account_code = upper(btrim(COALESCE(NULLIF(l.expense_account_code, ''), NULLIF(loc.cogs_account_code, ''), NULLIF(comp.cogs_account_code, ''), '')))
FROM inv_stock_transaction_lines l
JOIN inv_stock_transactions h
    ON l.transaction_id = h.id
LEFT JOIN inv_location_costing_settings loc
    ON loc.company_id = h.company_id
   AND loc.location_id = h.location_id
LEFT JOIN inv_company_settings comp
    ON comp.company_id = h.company_id
WHERE e.source_type = 'STOCK_OUT'
  AND l.id = e.source_line_id
  AND (e.cogs_account_code IS NULL OR btrim(e.cogs_account_code) = '');

UPDATE inv_cost_outbound_events e
SET cogs_account_code = upper(btrim(COALESCE(NULLIF(loc.cogs_account_code, ''), NULLIF(comp.cogs_account_code, ''), '')))
FROM inv_stock_transactions h
LEFT JOIN inv_location_costing_settings loc
    ON loc.company_id = h.company_id
   AND loc.location_id = h.location_id
LEFT JOIN inv_company_settings comp
    ON comp.company_id = h.company_id
WHERE e.source_type = 'STOCK_IN'
  AND h.id = e.source_id
  AND h.company_id = e.company_id
  AND (e.cogs_account_code IS NULL OR btrim(e.cogs_account_code) = '');

UPDATE inv_cost_outbound_events e
SET cogs_account_code = upper(btrim(COALESCE(NULLIF(loc.cogs_account_code, ''), NULLIF(comp.cogs_account_code, ''), '')))
FROM inv_stock_opname o
LEFT JOIN inv_location_costing_settings loc
    ON loc.company_id = o.company_id
   AND loc.location_id = o.location_id
LEFT JOIN inv_company_settings comp
    ON comp.company_id = o.company_id
WHERE e.source_type = 'OPNAME_MINUS'
  AND o.id = e.source_id
  AND o.company_id = e.company_id
  AND (e.cogs_account_code IS NULL OR btrim(e.cogs_account_code) = '');

UPDATE inv_cost_outbound_events e
SET cogs_account_code = upper(btrim(COALESCE(NULLIF(loc.cogs_account_code, ''), NULLIF(comp.cogs_account_code, ''), '')))
FROM inv_stock_opname o
LEFT JOIN inv_location_costing_settings loc
    ON loc.company_id = o.company_id
   AND loc.location_id = o.location_id
LEFT JOIN inv_company_settings comp
    ON comp.company_id = o.company_id
WHERE e.source_type = 'OPNAME_PLUS'
  AND o.id = e.source_id
  AND o.company_id = e.company_id
  AND (e.cogs_account_code IS NULL OR btrim(e.cogs_account_code) = '');

UPDATE inv_stock_transaction_lines
SET expense_account_code = upper(btrim(COALESCE(expense_account_code, '')))
WHERE expense_account_code IS NOT NULL;

UPDATE inv_cost_outbound_events
SET cogs_account_code = upper(btrim(COALESCE(cogs_account_code, '')))
WHERE cogs_account_code IS NOT NULL;
";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            _inventorySchemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
