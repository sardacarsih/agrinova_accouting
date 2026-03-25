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
    category_id BIGINT REFERENCES inv_categories(id) ON DELETE SET NULL,
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

CREATE TABLE IF NOT EXISTS inv_stock (
    id BIGSERIAL PRIMARY KEY,
    company_id BIGINT NOT NULL REFERENCES org_companies(id) ON DELETE RESTRICT,
    location_id BIGINT NOT NULL REFERENCES org_locations(id) ON DELETE RESTRICT,
    item_id BIGINT NOT NULL REFERENCES inv_items(id) ON DELETE RESTRICT,
    qty NUMERIC(18,4) NOT NULL DEFAULT 0,
    warehouse_id BIGINT REFERENCES inv_warehouses(id) ON DELETE SET NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inv_stock_item_location UNIQUE (company_id, location_id, item_id)
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
