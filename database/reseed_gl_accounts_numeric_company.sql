-- Numeric-only COA migration for one company.
-- Default rollout target: company_id = 1 (AGRINOVA).
-- The script updates mapped accounts in-place to preserve account_id references,
-- repoints duplicate legacy rows, inserts missing numeric nodes, and deactivates
-- remaining legacy rows that have no numeric counterpart.

BEGIN;

CREATE TEMP TABLE tmp_numeric_coa_templates (
    level_no INT NOT NULL,
    legacy_scope VARCHAR(10) NOT NULL,
    legacy_suffix VARCHAR(20) NOT NULL,
    new_code VARCHAR(20) NOT NULL,
    parent_new_code VARCHAR(20) NULL,
    account_name VARCHAR(200) NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    normal_balance CHAR(1) NOT NULL,
    sort_order INT NOT NULL,
    report_group VARCHAR(50) NOT NULL,
    cashflow_category VARCHAR(50) NOT NULL DEFAULT '',
    requires_department BOOLEAN NOT NULL DEFAULT FALSE,
    requires_project BOOLEAN NOT NULL DEFAULT FALSE,
    requires_cost_center BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT pk_tmp_numeric_coa_templates PRIMARY KEY (new_code)
) ON COMMIT DROP;

INSERT INTO tmp_numeric_coa_templates (
    level_no, legacy_scope, legacy_suffix, new_code, parent_new_code, account_name, account_type, normal_balance, sort_order, report_group, cashflow_category, requires_department, requires_project, requires_cost_center
)
VALUES
    (1, 'ALL', '10000.000', '10.00001.000', NULL, 'Assets', 'ASSET', 'D', 10, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (1, 'ALL', '20000.000', '10.00002.000', NULL, 'Liabilities', 'LIABILITY', 'C', 20, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (1, 'ALL', '30000.000', '10.00003.000', NULL, 'Equity', 'EQUITY', 'C', 30, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (1, 'ALL', '40000.000', '10.00004.000', NULL, 'Revenue', 'REVENUE', 'C', 40, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (1, 'ALL', '50000.000', '10.00005.000', NULL, 'Expenses', 'EXPENSE', 'D', 50, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),

    (2, 'ALL', '11000.000', '10.00101.000', '10.00001.000', 'Current Assets', 'ASSET', 'D', 110, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '12000.000', '10.00102.000', '10.00001.000', 'Non Current Assets', 'ASSET', 'D', 120, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '21000.000', '10.00201.000', '10.00002.000', 'Current Liabilities', 'LIABILITY', 'C', 210, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '22000.000', '10.00202.000', '10.00002.000', 'Non Current Liabilities', 'LIABILITY', 'C', 220, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '31000.000', '10.00301.000', '10.00003.000', 'Share Capital', 'EQUITY', 'C', 310, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '32000.000', '10.00302.000', '10.00003.000', 'Additional Equity', 'EQUITY', 'C', 320, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '33000.000', '10.00303.000', '10.00003.000', 'Retained Earnings', 'EQUITY', 'C', 330, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '41000.000', '10.00401.000', '10.00004.000', 'Operating Revenue', 'REVENUE', 'C', 410, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '42000.000', '10.00402.000', '10.00004.000', 'Other Revenue', 'REVENUE', 'C', 420, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '51000.000', '10.00501.000', '10.00005.000', 'Cost of Sales', 'EXPENSE', 'D', 510, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (2, 'KB',  '52000.000', '10.00502.000', '10.00005.000', 'Estate Direct Costs', 'EXPENSE', 'D', 520, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (2, 'PK',  '53000.000', '10.00503.000', '10.00005.000', 'Mill Direct Costs', 'EXPENSE', 'D', 530, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '54000.000', '10.00504.000', '10.00005.000', 'General and Administration', 'EXPENSE', 'D', 540, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '55000.000', '10.00505.000', '10.00005.000', 'Selling and Distribution Expenses', 'EXPENSE', 'D', 550, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (2, 'ALL', '56000.000', '10.00506.000', '10.00005.000', 'Finance and Other Expenses', 'EXPENSE', 'D', 560, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),

    (3, 'ALL', '11100.000', '10.01101.000', '10.00101.000', 'Cash and Bank', 'ASSET', 'D', 111, 'CASH_BANK', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11200.000', '10.01102.000', '10.00101.000', 'Trade Receivables', 'ASSET', 'D', 112, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11300.000', '10.01103.000', '10.00101.000', 'Other Receivables', 'ASSET', 'D', 113, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11400.000', '10.01104.000', '10.00101.000', 'Advances and Prepayments', 'ASSET', 'D', 114, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11500.000', '10.01105.000', '10.00101.000', 'Inventories and Supplies', 'ASSET', 'D', 115, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11600.000', '10.01106.000', '10.00101.000', 'Tax Assets', 'ASSET', 'D', 116, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '11700.000', '10.01107.000', '10.00101.000', 'Inter Unit Receivables', 'ASSET', 'D', 117, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '12100.000', '10.01201.000', '10.00102.000', 'Biological Assets and Plantation Development', 'ASSET', 'D', 121, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '12200.000', '10.01202.000', '10.00102.000', 'Fixed Assets', 'ASSET', 'D', 122, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '12300.000', '10.01203.000', '10.00102.000', 'Construction in Progress', 'ASSET', 'D', 123, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '21100.000', '10.02101.000', '10.00201.000', 'Trade Payables', 'LIABILITY', 'C', 211, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '21200.000', '10.02102.000', '10.00201.000', 'Accrued Expenses', 'LIABILITY', 'C', 212, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '21300.000', '10.02103.000', '10.00201.000', 'Taxes Payable', 'LIABILITY', 'C', 213, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '21400.000', '10.02104.000', '10.00201.000', 'Payroll Liabilities', 'LIABILITY', 'C', 214, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (3, 'ALL', '21500.000', '10.02105.000', '10.00201.000', 'Inter Unit Payables', 'LIABILITY', 'C', 215, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (3, 'KB',  '52100.000', '10.05121.000', '10.00502.000', 'Upkeep and Field Maintenance', 'EXPENSE', 'D', 521, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'KB',  '52200.000', '10.05122.000', '10.00502.000', 'Harvesting and Transport', 'EXPENSE', 'D', 522, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'KB',  '52300.000', '10.05123.000', '10.00502.000', 'Estate Labor', 'EXPENSE', 'D', 523, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'KB',  '52400.000', '10.05124.000', '10.00502.000', 'Workshop and Fleet', 'EXPENSE', 'D', 524, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'KB',  '52500.000', '10.05125.000', '10.00502.000', 'Nursery and Replant Support', 'EXPENSE', 'D', 525, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'PK',  '53100.000', '10.05131.000', '10.00503.000', 'Mill Process Materials', 'EXPENSE', 'D', 531, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'PK',  '53200.000', '10.05132.000', '10.00503.000', 'Utilities and Processing', 'EXPENSE', 'D', 532, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'PK',  '53300.000', '10.05133.000', '10.00503.000', 'Mill Labor', 'EXPENSE', 'D', 533, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'PK',  '53400.000', '10.05134.000', '10.00503.000', 'Maintenance and Workshop', 'EXPENSE', 'D', 534, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (3, 'PK',  '53500.000', '10.05135.000', '10.00503.000', 'Dispatch and Quality', 'EXPENSE', 'D', 535, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),

    (4, 'ALL', '11100.001', '10.01101.001', '10.01101.000', 'Kas Kecil', 'ASSET', 'D', 11101, 'CASH_BANK', 'OPERATING_CASH', FALSE, FALSE, FALSE),
    (4, 'ALL', '11100.002', '10.01101.002', '10.01101.000', 'Bank Operasional', 'ASSET', 'D', 11102, 'CASH_BANK', 'OPERATING_CASH', FALSE, FALSE, FALSE),
    (4, 'ALL', '11100.003', '10.01101.003', '10.01101.000', 'Bank Payroll', 'ASSET', 'D', 11103, 'CASH_BANK', 'OPERATING_CASH', FALSE, FALSE, FALSE),
    (4, 'ALL', '11200.001', '10.01102.001', '10.01102.000', 'Piutang Penjualan CPO', 'ASSET', 'D', 11201, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11200.002', '10.01102.002', '10.01102.000', 'Piutang Penjualan PK', 'ASSET', 'D', 11202, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11200.003', '10.01102.003', '10.01102.000', 'Piutang Penjualan TBS Eksternal', 'ASSET', 'D', 11203, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11300.001', '10.01103.001', '10.01103.000', 'Piutang Karyawan', 'ASSET', 'D', 11301, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11300.002', '10.01103.002', '10.01103.000', 'Piutang Afiliasi', 'ASSET', 'D', 11302, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11300.003', '10.01103.003', '10.01103.000', 'Piutang Lain-lain', 'ASSET', 'D', 11303, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11400.001', '10.01104.001', '10.01104.000', 'Uang Muka Pembelian', 'ASSET', 'D', 11401, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11400.002', '10.01104.002', '10.01104.000', 'Uang Muka Kontraktor', 'ASSET', 'D', 11402, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11400.003', '10.01104.003', '10.01104.000', 'Biaya Dibayar Dimuka', 'ASSET', 'D', 11403, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '11500.001', '10.01105.001', '10.01105.000', 'Persediaan Pupuk', 'ASSET', 'D', 11501, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '11500.002', '10.01105.002', '10.01105.000', 'Persediaan Pestisida dan Herbisida', 'ASSET', 'D', 11502, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '11500.003', '10.01105.003', '10.01105.000', 'Persediaan BBM dan Pelumas Kebun', 'ASSET', 'D', 11503, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '11500.004', '10.01105.004', '10.01105.000', 'Persediaan Sparepart Kebun', 'ASSET', 'D', 11504, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '11500.005', '10.01105.005', '10.01105.000', 'Persediaan Alat Panen dan APD', 'ASSET', 'D', 11505, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '11500.011', '10.01105.011', '10.01105.000', 'Persediaan TBS Pabrik', 'ASSET', 'D', 11511, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '11500.012', '10.01105.012', '10.01105.000', 'Persediaan CPO', 'ASSET', 'D', 11512, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '11500.013', '10.01105.013', '10.01105.000', 'Persediaan PK', 'ASSET', 'D', 11513, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '11500.014', '10.01105.014', '10.01105.000', 'Persediaan Chemical Pabrik', 'ASSET', 'D', 11514, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '11500.015', '10.01105.015', '10.01105.000', 'Persediaan Sparepart Pabrik', 'ASSET', 'D', 11515, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'HO',  '11500.021', '10.01105.021', '10.01105.000', 'Persediaan ATK dan Perlengkapan Kantor', 'ASSET', 'D', 11521, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11600.001', '10.01106.001', '10.01106.000', 'PPN Masukan', 'ASSET', 'D', 11601, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11600.002', '10.01106.002', '10.01106.000', 'PPh Dibayar Dimuka', 'ASSET', 'D', 11602, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '11700.001', '10.01107.001', '10.01107.000', 'Piutang Antar Unit', 'ASSET', 'D', 11701, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12100.001', '10.01201.001', '10.01201.000', 'Tanaman Belum Menghasilkan (TBM)', 'ASSET', 'D', 12101, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12100.002', '10.01201.002', '10.01201.000', 'Nursery Kapitalisasi', 'ASSET', 'D', 12102, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12100.003', '10.01201.003', '10.01201.000', 'Tanaman Menghasilkan (TM)', 'ASSET', 'D', 12103, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.001', '10.01202.001', '10.01202.000', 'Tanah', 'ASSET', 'D', 12201, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.002', '10.01202.002', '10.01202.000', 'Bangunan', 'ASSET', 'D', 12202, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.003', '10.01202.003', '10.01202.000', 'Jalan Jembatan dan Drainase', 'ASSET', 'D', 12203, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.004', '10.01202.004', '10.01202.000', 'Mesin dan Instalasi', 'ASSET', 'D', 12204, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.005', '10.01202.005', '10.01202.000', 'Kendaraan dan Alat Berat', 'ASSET', 'D', 12205, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12200.006', '10.01202.006', '10.01202.000', 'Peralatan Kantor dan IT', 'ASSET', 'D', 12206, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12300.001', '10.01203.001', '10.01203.000', 'CIP Infrastruktur Kebun', 'ASSET', 'D', 12301, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '12300.002', '10.01203.002', '10.01203.000', 'CIP Bangunan dan Fasilitas', 'ASSET', 'D', 12302, 'BS_ASSET', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21100.001', '10.02101.001', '10.02101.000', 'Hutang Usaha Vendor', 'LIABILITY', 'C', 21101, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21100.002', '10.02101.002', '10.02101.000', 'Hutang Kontraktor', 'LIABILITY', 'C', 21102, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21200.001', '10.02102.001', '10.02102.000', 'Akrual Gaji dan Upah', 'LIABILITY', 'C', 21201, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21200.002', '10.02102.002', '10.02102.000', 'Akrual Panen dan Angkut', 'LIABILITY', 'C', 21202, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21200.003', '10.02102.003', '10.02102.000', 'Akrual Listrik dan Utilitas', 'LIABILITY', 'C', 21203, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21300.001', '10.02103.001', '10.02103.000', 'PPN Keluaran', 'LIABILITY', 'C', 21301, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21300.002', '10.02103.002', '10.02103.000', 'Hutang PPh 21', 'LIABILITY', 'C', 21302, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21300.003', '10.02103.003', '10.02103.000', 'Hutang PPh 23 dan PPh Final', 'LIABILITY', 'C', 21303, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21400.001', '10.02104.001', '10.02104.000', 'Hutang Gaji', 'LIABILITY', 'C', 21401, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21400.002', '10.02104.002', '10.02104.000', 'Hutang BPJS', 'LIABILITY', 'C', 21402, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '21500.001', '10.02105.001', '10.02105.000', 'Hutang Antar Unit', 'LIABILITY', 'C', 21501, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '22000.001', '10.00202.001', '10.00202.000', 'Pinjaman Bank Jangka Panjang', 'LIABILITY', 'C', 22001, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '22000.002', '10.00202.002', '10.00202.000', 'Liabilitas Sewa', 'LIABILITY', 'C', 22002, 'BS_LIABILITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '31000.001', '10.00301.001', '10.00301.000', 'Modal Disetor', 'EQUITY', 'C', 31001, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '32000.001', '10.00302.001', '10.00302.000', 'Tambahan Modal Disetor', 'EQUITY', 'C', 32001, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '33000.001', '10.00303.001', '10.00303.000', 'Laba Ditahan', 'EQUITY', 'C', 33001, 'BS_EQUITY', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '41000.001', '10.00401.001', '10.00401.000', 'Penjualan CPO', 'REVENUE', 'C', 41001, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '41000.002', '10.00401.002', '10.00401.000', 'Penjualan PK', 'REVENUE', 'C', 41002, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '41000.003', '10.00401.003', '10.00401.000', 'Penjualan TBS Eksternal', 'REVENUE', 'C', 41003, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '42000.001', '10.00402.001', '10.00402.000', 'Pendapatan Jasa', 'REVENUE', 'C', 42001, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '42000.002', '10.00402.002', '10.00402.000', 'Pendapatan Sewa', 'REVENUE', 'C', 42002, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '42000.003', '10.00402.003', '10.00402.000', 'Pendapatan Lain-lain', 'REVENUE', 'C', 42003, 'PL_REVENUE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '51000.001', '10.00501.001', '10.00501.000', 'HPP Penjualan CPO', 'EXPENSE', 'D', 51001, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '51000.002', '10.00501.002', '10.00501.000', 'HPP Penjualan PK', 'EXPENSE', 'D', 51002, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'KB',  '52100.001', '10.05121.001', '10.05121.000', 'Beban Pemupukan', 'EXPENSE', 'D', 52101, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52100.002', '10.05121.002', '10.05121.000', 'Beban Pengendalian Gulma dan Hama', 'EXPENSE', 'D', 52102, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52100.003', '10.05121.003', '10.05121.000', 'Beban Perawatan Jalan dan Drainase', 'EXPENSE', 'D', 52103, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52100.004', '10.05121.004', '10.05121.000', 'Beban Pemeliharaan Tanaman', 'EXPENSE', 'D', 52104, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52200.001', '10.05122.001', '10.05122.000', 'Beban Panen', 'EXPENSE', 'D', 52201, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52200.002', '10.05122.002', '10.05122.000', 'Beban Muat TBS', 'EXPENSE', 'D', 52202, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52200.003', '10.05122.003', '10.05122.000', 'Beban Angkut TBS', 'EXPENSE', 'D', 52203, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52300.001', '10.05123.001', '10.05123.000', 'Gaji dan Upah Kebun', 'EXPENSE', 'D', 52301, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52300.002', '10.05123.002', '10.05123.000', 'Lembur dan Tunjangan Kebun', 'EXPENSE', 'D', 52302, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52300.003', '10.05123.003', '10.05123.000', 'Kesejahteraan Karyawan Kebun', 'EXPENSE', 'D', 52303, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52400.001', '10.05124.001', '10.05124.000', 'BBM dan Pelumas Alat Kebun', 'EXPENSE', 'D', 52401, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52400.002', '10.05124.002', '10.05124.000', 'Sparepart dan Bengkel Kebun', 'EXPENSE', 'D', 52402, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52400.003', '10.05124.003', '10.05124.000', 'Sewa Alat dan Kendaraan Kebun', 'EXPENSE', 'D', 52403, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52500.001', '10.05125.001', '10.05125.000', 'Beban Nursery Non Kapital', 'EXPENSE', 'D', 52501, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'KB',  '52500.002', '10.05125.002', '10.05125.000', 'Dukungan Replanting Non Kapital', 'EXPENSE', 'D', 52502, 'PL_EXPENSE', '', FALSE, FALSE, TRUE),
    (4, 'PK',  '53100.001', '10.05131.001', '10.05131.000', 'Bahan Penolong Pabrik', 'EXPENSE', 'D', 53101, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53100.002', '10.05131.002', '10.05131.000', 'Chemical Water Treatment', 'EXPENSE', 'D', 53102, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53100.003', '10.05131.003', '10.05131.000', 'Material Laboratorium', 'EXPENSE', 'D', 53103, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53200.001', '10.05132.001', '10.05132.000', 'Listrik dan Steam', 'EXPENSE', 'D', 53201, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53200.002', '10.05132.002', '10.05132.000', 'Air dan Pengolahan Limbah', 'EXPENSE', 'D', 53202, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53200.003', '10.05132.003', '10.05132.000', 'BBM Genset dan Boiler', 'EXPENSE', 'D', 53203, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53300.001', '10.05133.001', '10.05133.000', 'Gaji dan Upah Pabrik', 'EXPENSE', 'D', 53301, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53300.002', '10.05133.002', '10.05133.000', 'Lembur dan Tunjangan Pabrik', 'EXPENSE', 'D', 53302, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53400.001', '10.05134.001', '10.05134.000', 'Maintenance Mesin Pabrik', 'EXPENSE', 'D', 53401, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53400.002', '10.05134.002', '10.05134.000', 'Sparepart dan Workshop Pabrik', 'EXPENSE', 'D', 53402, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53400.003', '10.05134.003', '10.05134.000', 'Sewa Alat Pabrik', 'EXPENSE', 'D', 53403, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53500.001', '10.05135.001', '10.05135.000', 'Dispatch Bulking dan Storage', 'EXPENSE', 'D', 53501, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'PK',  '53500.002', '10.05135.002', '10.05135.000', 'Quality Control dan Laboratorium', 'EXPENSE', 'D', 53502, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.001', '10.00504.001', '10.00504.000', 'Gaji dan Tunjangan Administrasi', 'EXPENSE', 'D', 54001, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.002', '10.00504.002', '10.00504.000', 'ATK dan Perlengkapan Kantor', 'EXPENSE', 'D', 54002, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.003', '10.00504.003', '10.00504.000', 'Telekomunikasi dan IT', 'EXPENSE', 'D', 54003, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.004', '10.00504.004', '10.00504.000', 'Perjalanan Dinas', 'EXPENSE', 'D', 54004, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.005', '10.00504.005', '10.00504.000', 'Jasa Profesional dan Audit', 'EXPENSE', 'D', 54005, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.006', '10.00504.006', '10.00504.000', 'Keamanan dan Kebersihan', 'EXPENSE', 'D', 54006, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.007', '10.00504.007', '10.00504.000', 'Rekrutmen dan Pelatihan', 'EXPENSE', 'D', 54007, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '54000.008', '10.00504.008', '10.00504.000', 'Perizinan dan Pajak Daerah', 'EXPENSE', 'D', 54008, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '55000.001', '10.00505.001', '10.00505.000', 'Angkut Penjualan dan Freight', 'EXPENSE', 'D', 55001, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '55000.002', '10.00505.002', '10.00505.000', 'Loading dan Port Charges', 'EXPENSE', 'D', 55002, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '55000.003', '10.00505.003', '10.00505.000', 'Komisi Penjualan', 'EXPENSE', 'D', 55003, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '56000.001', '10.00506.001', '10.00506.000', 'Beban Bunga', 'EXPENSE', 'D', 56001, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '56000.002', '10.00506.002', '10.00506.000', 'Administrasi Bank', 'EXPENSE', 'D', 56002, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '56000.003', '10.00506.003', '10.00506.000', 'Rugi Selisih Kurs', 'EXPENSE', 'D', 56003, 'PL_EXPENSE', '', FALSE, FALSE, FALSE),
    (4, 'ALL', '56000.004', '10.00506.004', '10.00506.000', 'Beban Lain-lain', 'EXPENSE', 'D', 56004, 'PL_EXPENSE', '', FALSE, FALSE, FALSE);

CREATE TEMP TABLE tmp_numeric_coa_targets (
    account_code VARCHAR(20) PRIMARY KEY,
    account_id BIGINT NOT NULL
) ON COMMIT DROP;

DO $$
DECLARE
    v_company_id BIGINT := 1;
    v_template RECORD;
    v_account_id BIGINT;
    v_parent_id BIGINT;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM org_companies
        WHERE id = v_company_id
    ) THEN
        RAISE EXCEPTION 'Company % not found.', v_company_id;
    END IF;

    FOR v_template IN
        SELECT *
        FROM tmp_numeric_coa_templates
        ORDER BY level_no, sort_order, new_code
    LOOP
        SELECT a.id
        INTO v_account_id
        FROM gl_accounts a
        WHERE a.company_id = v_company_id
          AND (
                UPPER(a.account_code) = UPPER(v_template.new_code)
                OR (v_template.legacy_scope = 'ALL' AND UPPER(a.account_code) IN (
                    'HO.' || UPPER(v_template.legacy_suffix),
                    'PK.' || UPPER(v_template.legacy_suffix),
                    'KB.' || UPPER(v_template.legacy_suffix)))
                OR (v_template.legacy_scope = 'HO' AND UPPER(a.account_code) = 'HO.' || UPPER(v_template.legacy_suffix))
                OR (v_template.legacy_scope = 'PK' AND UPPER(a.account_code) = 'PK.' || UPPER(v_template.legacy_suffix))
                OR (v_template.legacy_scope = 'KB' AND UPPER(a.account_code) = 'KB.' || UPPER(v_template.legacy_suffix))
              )
        ORDER BY
            CASE
                WHEN UPPER(a.account_code) = UPPER(v_template.new_code) THEN 0
                ELSE 1
            END,
            a.id
        LIMIT 1;

        IF v_template.parent_new_code IS NULL THEN
            v_parent_id := NULL;
        ELSE
            SELECT account_id
            INTO v_parent_id
            FROM tmp_numeric_coa_targets
            WHERE account_code = v_template.parent_new_code;
        END IF;

        IF v_account_id IS NULL THEN
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
                cashflow_category,
                requires_department,
                requires_project,
                requires_cost_center,
                created_by,
                updated_by,
                created_at,
                updated_at
            )
            VALUES (
                v_company_id,
                v_template.new_code,
                v_template.account_name,
                v_template.account_type,
                v_template.normal_balance,
                v_parent_id,
                v_template.level_no,
                split_part(v_template.new_code, '.', 3) <> '000',
                TRUE,
                v_template.sort_order,
                v_template.report_group,
                v_template.cashflow_category,
                v_template.requires_department,
                v_template.requires_project,
                v_template.requires_cost_center,
                'MIGRATION',
                'MIGRATION',
                NOW(),
                NOW()
            )
            RETURNING id INTO v_account_id;
        ELSE
            UPDATE gl_accounts
            SET account_code = v_template.new_code,
                account_name = v_template.account_name,
                account_type = v_template.account_type,
                normal_balance = v_template.normal_balance,
                parent_account_id = v_parent_id,
                hierarchy_level = v_template.level_no,
                is_posting = split_part(v_template.new_code, '.', 3) <> '000',
                is_active = TRUE,
                sort_order = v_template.sort_order,
                report_group = v_template.report_group,
                cashflow_category = v_template.cashflow_category,
                requires_department = v_template.requires_department,
                requires_project = v_template.requires_project,
                requires_cost_center = v_template.requires_cost_center,
                updated_by = 'MIGRATION',
                updated_at = NOW()
            WHERE id = v_account_id;
        END IF;

        INSERT INTO tmp_numeric_coa_targets (account_code, account_id)
        VALUES (v_template.new_code, v_account_id)
        ON CONFLICT (account_code) DO UPDATE
        SET account_id = EXCLUDED.account_id;
    END LOOP;
END
$$;

CREATE TEMP TABLE tmp_numeric_coa_losers ON COMMIT DROP AS
SELECT DISTINCT
       t.new_code,
       a.id AS loser_id,
       target.account_id AS survivor_id
FROM tmp_numeric_coa_templates t
JOIN tmp_numeric_coa_targets target ON target.account_code = t.new_code
JOIN gl_accounts a ON a.company_id = 1
                 AND (
                        UPPER(a.account_code) = UPPER(t.new_code)
                        OR (t.legacy_scope = 'ALL' AND UPPER(a.account_code) IN (
                            'HO.' || UPPER(t.legacy_suffix),
                            'PK.' || UPPER(t.legacy_suffix),
                            'KB.' || UPPER(t.legacy_suffix)))
                        OR (t.legacy_scope = 'HO' AND UPPER(a.account_code) = 'HO.' || UPPER(t.legacy_suffix))
                        OR (t.legacy_scope = 'PK' AND UPPER(a.account_code) = 'PK.' || UPPER(t.legacy_suffix))
                        OR (t.legacy_scope = 'KB' AND UPPER(a.account_code) = 'KB.' || UPPER(t.legacy_suffix))
                     )
WHERE a.id <> target.account_id;

UPDATE gl_journal_details d
SET account_id = l.survivor_id,
    updated_at = NOW()
FROM tmp_numeric_coa_losers l
WHERE d.account_id = l.loser_id;

UPDATE gl_ledger_entries le
SET account_id = l.survivor_id,
    updated_at = NOW()
FROM tmp_numeric_coa_losers l
WHERE le.account_id = l.loser_id;

UPDATE gl_accounts a
SET parent_account_id = NULL,
    is_active = FALSE,
    updated_by = 'MIGRATION',
    updated_at = NOW()
FROM tmp_numeric_coa_losers l
WHERE a.id = l.loser_id;

UPDATE gl_accounts a
SET hierarchy_level = t.level_no,
    is_posting = split_part(t.new_code, '.', 3) <> '000',
    updated_by = 'MIGRATION',
    updated_at = NOW()
FROM tmp_numeric_coa_templates t
JOIN tmp_numeric_coa_targets target ON target.account_code = t.new_code
WHERE a.id = target.account_id;

CREATE TEMP TABLE tmp_numeric_coa_unmapped_existing ON COMMIT DROP AS
SELECT a.id
FROM gl_accounts a
WHERE a.company_id = 1
  AND NOT EXISTS (
      SELECT 1
      FROM tmp_numeric_coa_targets t
      WHERE t.account_id = a.id);

UPDATE gl_accounts a
SET parent_account_id = NULL,
    is_active = FALSE,
    updated_by = 'MIGRATION',
    updated_at = NOW()
FROM tmp_numeric_coa_unmapped_existing u
WHERE a.id = u.id;

COMMIT;
