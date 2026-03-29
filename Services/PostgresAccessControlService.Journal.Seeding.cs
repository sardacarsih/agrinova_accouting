using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private sealed record SeedAccountTemplate(
        int Level,
        string Scope,
        string CodeSuffix,
        string? ParentSuffix,
        string Name,
        string AccountType,
        string NormalBalance,
        int SortOrder,
        string ReportGroup,
        string CashflowCategory = "",
        bool RequiresDepartment = false,
        bool RequiresProject = false,
        bool RequiresCostCenter = false);

    private static readonly SeedAccountTemplate[] PalmOilSeedAccountTemplates =
    [
        new(1, "ALL", "10000.000", null, "Assets", "ASSET", "D", 10, "BS_ASSET"),
        new(1, "ALL", "20000.000", null, "Liabilities", "LIABILITY", "C", 20, "BS_LIABILITY"),
        new(1, "ALL", "30000.000", null, "Equity", "EQUITY", "C", 30, "BS_EQUITY"),
        new(1, "ALL", "40000.000", null, "Revenue", "REVENUE", "C", 40, "PL_REVENUE"),
        new(1, "ALL", "50000.000", null, "Expenses", "EXPENSE", "D", 50, "PL_EXPENSE"),

        new(2, "ALL", "11000.000", "10000.000", "Current Assets", "ASSET", "D", 110, "BS_ASSET"),
        new(2, "ALL", "12000.000", "10000.000", "Non Current Assets", "ASSET", "D", 120, "BS_ASSET"),
        new(2, "ALL", "21000.000", "20000.000", "Current Liabilities", "LIABILITY", "C", 210, "BS_LIABILITY"),
        new(2, "ALL", "22000.000", "20000.000", "Non Current Liabilities", "LIABILITY", "C", 220, "BS_LIABILITY"),
        new(2, "ALL", "31000.000", "30000.000", "Share Capital", "EQUITY", "C", 310, "BS_EQUITY"),
        new(2, "ALL", "32000.000", "30000.000", "Additional Equity", "EQUITY", "C", 320, "BS_EQUITY"),
        new(2, "ALL", "33000.000", "30000.000", "Retained Earnings", "EQUITY", "C", 330, "BS_EQUITY"),
        new(2, "ALL", "41000.000", "40000.000", "Operating Revenue", "REVENUE", "C", 410, "PL_REVENUE"),
        new(2, "ALL", "42000.000", "40000.000", "Other Revenue", "REVENUE", "C", 420, "PL_REVENUE"),
        new(2, "ALL", "51000.000", "50000.000", "Cost of Sales", "EXPENSE", "D", 510, "PL_EXPENSE"),
        new(2, "KB", "52000.000", "50000.000", "Estate Direct Costs", "EXPENSE", "D", 520, "PL_EXPENSE"),
        new(2, "PK", "53000.000", "50000.000", "Mill Direct Costs", "EXPENSE", "D", 530, "PL_EXPENSE"),
        new(2, "ALL", "54000.000", "50000.000", "General and Administration", "EXPENSE", "D", 540, "PL_EXPENSE"),
        new(2, "ALL", "55000.000", "50000.000", "Selling and Distribution Expenses", "EXPENSE", "D", 550, "PL_EXPENSE"),
        new(2, "ALL", "56000.000", "50000.000", "Finance and Other Expenses", "EXPENSE", "D", 560, "PL_EXPENSE"),

        new(3, "ALL", "11100.000", "11000.000", "Cash and Bank", "ASSET", "D", 111, "CASH_BANK"),
        new(3, "ALL", "11200.000", "11000.000", "Trade Receivables", "ASSET", "D", 112, "BS_ASSET"),
        new(3, "ALL", "11300.000", "11000.000", "Other Receivables", "ASSET", "D", 113, "BS_ASSET"),
        new(3, "ALL", "11400.000", "11000.000", "Advances and Prepayments", "ASSET", "D", 114, "BS_ASSET"),
        new(3, "ALL", "11500.000", "11000.000", "Inventories and Supplies", "ASSET", "D", 115, "BS_ASSET"),
        new(3, "ALL", "11600.000", "11000.000", "Tax Assets", "ASSET", "D", 116, "BS_ASSET"),
        new(3, "ALL", "11700.000", "11000.000", "Inter Unit Receivables", "ASSET", "D", 117, "BS_ASSET"),
        new(3, "ALL", "12100.000", "12000.000", "Biological Assets and Plantation Development", "ASSET", "D", 121, "BS_ASSET"),
        new(3, "ALL", "12200.000", "12000.000", "Fixed Assets", "ASSET", "D", 122, "BS_ASSET"),
        new(3, "ALL", "12300.000", "12000.000", "Construction in Progress", "ASSET", "D", 123, "BS_ASSET"),
        new(3, "ALL", "21100.000", "21000.000", "Trade Payables", "LIABILITY", "C", 211, "BS_LIABILITY"),
        new(3, "ALL", "21200.000", "21000.000", "Accrued Expenses", "LIABILITY", "C", 212, "BS_LIABILITY"),
        new(3, "ALL", "21300.000", "21000.000", "Taxes Payable", "LIABILITY", "C", 213, "BS_LIABILITY"),
        new(3, "ALL", "21400.000", "21000.000", "Payroll Liabilities", "LIABILITY", "C", 214, "BS_LIABILITY"),
        new(3, "ALL", "21500.000", "21000.000", "Inter Unit Payables", "LIABILITY", "C", 215, "BS_LIABILITY"),
        new(3, "KB", "52100.000", "52000.000", "Upkeep and Field Maintenance", "EXPENSE", "D", 521, "PL_EXPENSE"),
        new(3, "KB", "52200.000", "52000.000", "Harvesting and Transport", "EXPENSE", "D", 522, "PL_EXPENSE"),
        new(3, "KB", "52300.000", "52000.000", "Estate Labor", "EXPENSE", "D", 523, "PL_EXPENSE"),
        new(3, "KB", "52400.000", "52000.000", "Workshop and Fleet", "EXPENSE", "D", 524, "PL_EXPENSE"),
        new(3, "KB", "52500.000", "52000.000", "Nursery and Replant Support", "EXPENSE", "D", 525, "PL_EXPENSE"),
        new(3, "PK", "53100.000", "53000.000", "Mill Process Materials", "EXPENSE", "D", 531, "PL_EXPENSE"),
        new(3, "PK", "53200.000", "53000.000", "Utilities and Processing", "EXPENSE", "D", 532, "PL_EXPENSE"),
        new(3, "PK", "53300.000", "53000.000", "Mill Labor", "EXPENSE", "D", 533, "PL_EXPENSE"),
        new(3, "PK", "53400.000", "53000.000", "Maintenance and Workshop", "EXPENSE", "D", 534, "PL_EXPENSE"),
        new(3, "PK", "53500.000", "53000.000", "Dispatch and Quality", "EXPENSE", "D", 535, "PL_EXPENSE"),

        new(4, "ALL", "11100.001", "11100.000", "Kas Kecil", "ASSET", "D", 11101, "CASH_BANK", "OPERATING_CASH"),
        new(4, "ALL", "11100.002", "11100.000", "Bank Operasional", "ASSET", "D", 11102, "CASH_BANK", "OPERATING_CASH"),
        new(4, "ALL", "11100.003", "11100.000", "Bank Payroll", "ASSET", "D", 11103, "CASH_BANK", "OPERATING_CASH"),
        new(4, "ALL", "11200.001", "11200.000", "Piutang Penjualan CPO", "ASSET", "D", 11201, "BS_ASSET"),
        new(4, "ALL", "11200.002", "11200.000", "Piutang Penjualan PK", "ASSET", "D", 11202, "BS_ASSET"),
        new(4, "ALL", "11200.003", "11200.000", "Piutang Penjualan TBS Eksternal", "ASSET", "D", 11203, "BS_ASSET"),
        new(4, "ALL", "11300.001", "11300.000", "Piutang Karyawan", "ASSET", "D", 11301, "BS_ASSET"),
        new(4, "ALL", "11300.002", "11300.000", "Piutang Afiliasi", "ASSET", "D", 11302, "BS_ASSET"),
        new(4, "ALL", "11300.003", "11300.000", "Piutang Lain-lain", "ASSET", "D", 11303, "BS_ASSET"),
        new(4, "ALL", "11400.001", "11400.000", "Uang Muka Pembelian", "ASSET", "D", 11401, "BS_ASSET"),
        new(4, "ALL", "11400.002", "11400.000", "Uang Muka Kontraktor", "ASSET", "D", 11402, "BS_ASSET"),
        new(4, "ALL", "11400.003", "11400.000", "Biaya Dibayar Dimuka", "ASSET", "D", 11403, "BS_ASSET"),
        new(4, "HO", "11500.021", "11500.000", "Persediaan ATK dan Perlengkapan HO", "ASSET", "D", 11521, "BS_ASSET"),
        new(4, "KB", "11500.001", "11500.000", "Persediaan Pupuk", "ASSET", "D", 11501, "BS_ASSET"),
        new(4, "KB", "11500.002", "11500.000", "Persediaan Pestisida dan Herbisida", "ASSET", "D", 11502, "BS_ASSET"),
        new(4, "KB", "11500.003", "11500.000", "Persediaan BBM dan Pelumas Kebun", "ASSET", "D", 11503, "BS_ASSET"),
        new(4, "KB", "11500.004", "11500.000", "Persediaan Sparepart Kebun", "ASSET", "D", 11504, "BS_ASSET"),
        new(4, "KB", "11500.005", "11500.000", "Persediaan Alat Panen dan APD", "ASSET", "D", 11505, "BS_ASSET"),
        new(4, "PK", "11500.011", "11500.000", "Persediaan TBS Pabrik", "ASSET", "D", 11511, "BS_ASSET"),
        new(4, "PK", "11500.012", "11500.000", "Persediaan CPO", "ASSET", "D", 11512, "BS_ASSET"),
        new(4, "PK", "11500.013", "11500.000", "Persediaan PK", "ASSET", "D", 11513, "BS_ASSET"),
        new(4, "PK", "11500.014", "11500.000", "Persediaan Chemical Pabrik", "ASSET", "D", 11514, "BS_ASSET"),
        new(4, "PK", "11500.015", "11500.000", "Persediaan Sparepart Pabrik", "ASSET", "D", 11515, "BS_ASSET"),
        new(4, "ALL", "11600.001", "11600.000", "PPN Masukan", "ASSET", "D", 11601, "BS_ASSET"),
        new(4, "ALL", "11600.002", "11600.000", "PPh Dibayar Dimuka", "ASSET", "D", 11602, "BS_ASSET"),
        new(4, "ALL", "11700.001", "11700.000", "Piutang Antar Unit", "ASSET", "D", 11701, "BS_ASSET"),
        new(4, "ALL", "12100.001", "12100.000", "Tanaman Belum Menghasilkan (TBM)", "ASSET", "D", 12101, "BS_ASSET"),
        new(4, "ALL", "12100.002", "12100.000", "Nursery Kapitalisasi", "ASSET", "D", 12102, "BS_ASSET"),
        new(4, "ALL", "12100.003", "12100.000", "Tanaman Menghasilkan (TM)", "ASSET", "D", 12103, "BS_ASSET"),
        new(4, "ALL", "12200.001", "12200.000", "Tanah", "ASSET", "D", 12201, "BS_ASSET"),
        new(4, "ALL", "12200.002", "12200.000", "Bangunan", "ASSET", "D", 12202, "BS_ASSET"),
        new(4, "ALL", "12200.003", "12200.000", "Jalan Jembatan dan Drainase", "ASSET", "D", 12203, "BS_ASSET"),
        new(4, "ALL", "12200.004", "12200.000", "Mesin dan Instalasi", "ASSET", "D", 12204, "BS_ASSET"),
        new(4, "ALL", "12200.005", "12200.000", "Kendaraan dan Alat Berat", "ASSET", "D", 12205, "BS_ASSET"),
        new(4, "ALL", "12200.006", "12200.000", "Peralatan Kantor dan IT", "ASSET", "D", 12206, "BS_ASSET"),
        new(4, "ALL", "12300.001", "12300.000", "CIP Infrastruktur Kebun", "ASSET", "D", 12301, "BS_ASSET"),
        new(4, "ALL", "12300.002", "12300.000", "CIP Bangunan dan Fasilitas", "ASSET", "D", 12302, "BS_ASSET"),
        new(4, "ALL", "21100.001", "21100.000", "Hutang Usaha Vendor", "LIABILITY", "C", 21101, "BS_LIABILITY"),
        new(4, "ALL", "21100.002", "21100.000", "Hutang Kontraktor", "LIABILITY", "C", 21102, "BS_LIABILITY"),
        new(4, "ALL", "21200.001", "21200.000", "Akrual Gaji dan Upah", "LIABILITY", "C", 21201, "BS_LIABILITY"),
        new(4, "ALL", "21200.002", "21200.000", "Akrual Panen dan Angkut", "LIABILITY", "C", 21202, "BS_LIABILITY"),
        new(4, "ALL", "21200.003", "21200.000", "Akrual Listrik dan Utilitas", "LIABILITY", "C", 21203, "BS_LIABILITY"),
        new(4, "ALL", "21300.001", "21300.000", "PPN Keluaran", "LIABILITY", "C", 21301, "BS_LIABILITY"),
        new(4, "ALL", "21300.002", "21300.000", "Hutang PPh 21", "LIABILITY", "C", 21302, "BS_LIABILITY"),
        new(4, "ALL", "21300.003", "21300.000", "Hutang PPh 23 dan PPh Final", "LIABILITY", "C", 21303, "BS_LIABILITY"),
        new(4, "ALL", "21400.001", "21400.000", "Hutang Gaji", "LIABILITY", "C", 21401, "BS_LIABILITY"),
        new(4, "ALL", "21400.002", "21400.000", "Hutang BPJS", "LIABILITY", "C", 21402, "BS_LIABILITY"),
        new(4, "ALL", "21500.001", "21500.000", "Hutang Antar Unit", "LIABILITY", "C", 21501, "BS_LIABILITY"),
        new(4, "ALL", "22000.001", "22000.000", "Pinjaman Bank Jangka Panjang", "LIABILITY", "C", 22001, "BS_LIABILITY"),
        new(4, "ALL", "22000.002", "22000.000", "Liabilitas Sewa", "LIABILITY", "C", 22002, "BS_LIABILITY"),
        new(4, "ALL", "31000.001", "31000.000", "Modal Disetor", "EQUITY", "C", 31001, "BS_EQUITY"),
        new(4, "ALL", "32000.001", "32000.000", "Tambahan Modal Disetor", "EQUITY", "C", 32001, "BS_EQUITY"),
        new(4, "ALL", "33000.001", "33000.000", "Laba Ditahan", "EQUITY", "C", 33001, "BS_EQUITY"),
        new(4, "ALL", "41000.001", "41000.000", "Penjualan CPO", "REVENUE", "C", 41001, "PL_REVENUE"),
        new(4, "ALL", "41000.002", "41000.000", "Penjualan PK", "REVENUE", "C", 41002, "PL_REVENUE"),
        new(4, "ALL", "41000.003", "41000.000", "Penjualan TBS Eksternal", "REVENUE", "C", 41003, "PL_REVENUE"),
        new(4, "ALL", "42000.001", "42000.000", "Pendapatan Jasa", "REVENUE", "C", 42001, "PL_REVENUE"),
        new(4, "ALL", "42000.002", "42000.000", "Pendapatan Sewa", "REVENUE", "C", 42002, "PL_REVENUE"),
        new(4, "ALL", "42000.003", "42000.000", "Pendapatan Lain-lain", "REVENUE", "C", 42003, "PL_REVENUE"),
        new(4, "ALL", "51000.001", "51000.000", "HPP Penjualan CPO", "EXPENSE", "D", 51001, "PL_EXPENSE"),
        new(4, "ALL", "51000.002", "51000.000", "HPP Penjualan PK", "EXPENSE", "D", 51002, "PL_EXPENSE"),
        new(4, "KB", "52100.001", "52100.000", "Beban Pemupukan", "EXPENSE", "D", 52101, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52100.002", "52100.000", "Beban Pengendalian Gulma dan Hama", "EXPENSE", "D", 52102, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52100.003", "52100.000", "Beban Perawatan Jalan dan Drainase", "EXPENSE", "D", 52103, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52100.004", "52100.000", "Beban Pemeliharaan Tanaman", "EXPENSE", "D", 52104, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52200.001", "52200.000", "Beban Panen", "EXPENSE", "D", 52201, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52200.002", "52200.000", "Beban Muat TBS", "EXPENSE", "D", 52202, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52200.003", "52200.000", "Beban Angkut TBS", "EXPENSE", "D", 52203, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52300.001", "52300.000", "Gaji dan Upah Kebun", "EXPENSE", "D", 52301, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52300.002", "52300.000", "Lembur dan Tunjangan Kebun", "EXPENSE", "D", 52302, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52300.003", "52300.000", "Kesejahteraan Karyawan Kebun", "EXPENSE", "D", 52303, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52400.001", "52400.000", "BBM dan Pelumas Alat Kebun", "EXPENSE", "D", 52401, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52400.002", "52400.000", "Sparepart dan Bengkel Kebun", "EXPENSE", "D", 52402, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52400.003", "52400.000", "Sewa Alat dan Kendaraan Kebun", "EXPENSE", "D", 52403, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52500.001", "52500.000", "Beban Nursery Non Kapital", "EXPENSE", "D", 52501, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "KB", "52500.002", "52500.000", "Dukungan Replanting Non Kapital", "EXPENSE", "D", 52502, "PL_EXPENSE", RequiresCostCenter: true),
        new(4, "PK", "53100.001", "53100.000", "Bahan Penolong Pabrik", "EXPENSE", "D", 53101, "PL_EXPENSE"),
        new(4, "PK", "53100.002", "53100.000", "Chemical Water Treatment", "EXPENSE", "D", 53102, "PL_EXPENSE"),
        new(4, "PK", "53100.003", "53100.000", "Material Laboratorium", "EXPENSE", "D", 53103, "PL_EXPENSE"),
        new(4, "PK", "53200.001", "53200.000", "Listrik dan Steam", "EXPENSE", "D", 53201, "PL_EXPENSE"),
        new(4, "PK", "53200.002", "53200.000", "Air dan Pengolahan Limbah", "EXPENSE", "D", 53202, "PL_EXPENSE"),
        new(4, "PK", "53200.003", "53200.000", "BBM Genset dan Boiler", "EXPENSE", "D", 53203, "PL_EXPENSE"),
        new(4, "PK", "53300.001", "53300.000", "Gaji dan Upah Pabrik", "EXPENSE", "D", 53301, "PL_EXPENSE"),
        new(4, "PK", "53300.002", "53300.000", "Lembur dan Tunjangan Pabrik", "EXPENSE", "D", 53302, "PL_EXPENSE"),
        new(4, "PK", "53400.001", "53400.000", "Maintenance Mesin Pabrik", "EXPENSE", "D", 53401, "PL_EXPENSE"),
        new(4, "PK", "53400.002", "53400.000", "Sparepart dan Workshop Pabrik", "EXPENSE", "D", 53402, "PL_EXPENSE"),
        new(4, "PK", "53400.003", "53400.000", "Sewa Alat Pabrik", "EXPENSE", "D", 53403, "PL_EXPENSE"),
        new(4, "PK", "53500.001", "53500.000", "Dispatch Bulking dan Storage", "EXPENSE", "D", 53501, "PL_EXPENSE"),
        new(4, "PK", "53500.002", "53500.000", "Quality Control dan Laboratorium", "EXPENSE", "D", 53502, "PL_EXPENSE"),
        new(4, "ALL", "54000.001", "54000.000", "Gaji dan Tunjangan Administrasi", "EXPENSE", "D", 54001, "PL_EXPENSE"),
        new(4, "ALL", "54000.002", "54000.000", "ATK dan Perlengkapan Kantor", "EXPENSE", "D", 54002, "PL_EXPENSE"),
        new(4, "ALL", "54000.003", "54000.000", "Telekomunikasi dan IT", "EXPENSE", "D", 54003, "PL_EXPENSE"),
        new(4, "ALL", "54000.004", "54000.000", "Perjalanan Dinas", "EXPENSE", "D", 54004, "PL_EXPENSE"),
        new(4, "ALL", "54000.005", "54000.000", "Jasa Profesional dan Audit", "EXPENSE", "D", 54005, "PL_EXPENSE"),
        new(4, "ALL", "54000.006", "54000.000", "Keamanan dan Kebersihan", "EXPENSE", "D", 54006, "PL_EXPENSE"),
        new(4, "ALL", "54000.007", "54000.000", "Rekrutmen dan Pelatihan", "EXPENSE", "D", 54007, "PL_EXPENSE"),
        new(4, "ALL", "54000.008", "54000.000", "Perizinan dan Pajak Daerah", "EXPENSE", "D", 54008, "PL_EXPENSE"),
        new(4, "ALL", "55000.001", "55000.000", "Angkut Penjualan dan Freight", "EXPENSE", "D", 55001, "PL_EXPENSE"),
        new(4, "ALL", "55000.002", "55000.000", "Loading dan Port Charges", "EXPENSE", "D", 55002, "PL_EXPENSE"),
        new(4, "ALL", "55000.003", "55000.000", "Komisi Penjualan", "EXPENSE", "D", 55003, "PL_EXPENSE"),
        new(4, "ALL", "56000.001", "56000.000", "Beban Bunga", "EXPENSE", "D", 56001, "PL_EXPENSE"),
        new(4, "ALL", "56000.002", "56000.000", "Administrasi Bank", "EXPENSE", "D", 56002, "PL_EXPENSE"),
        new(4, "ALL", "56000.003", "56000.000", "Rugi Selisih Kurs", "EXPENSE", "D", 56003, "PL_EXPENSE"),
        new(4, "ALL", "56000.004", "56000.000", "Beban Lain-lain", "EXPENSE", "D", 56004, "PL_EXPENSE")
    ];

    private async Task SeedPalmOilSampleAccountsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var locationsByCompany = new Dictionary<long, HashSet<string>>();
        await using var locationCommand = new NpgsqlCommand(@"
SELECT DISTINCT
       c.id AS company_id,
       CASE
           WHEN upper(btrim(l.code)) IN ('HO', 'HQ') THEN 'HO'
           WHEN upper(btrim(l.code)) IN ('PK', 'PKS') THEN 'PK'
           WHEN upper(btrim(l.code)) IN ('KB', 'KEBUN') THEN 'KB'
           ELSE left(upper(btrim(l.code)), 2)
       END AS location_prefix
FROM org_companies c
JOIN org_locations l ON l.company_id = c.id
WHERE c.is_active = TRUE
  AND l.is_active = TRUE;", connection);
        await using (var reader = await locationCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var companyId = reader.GetInt64(0);
                var prefix = reader.GetString(1);
                if (!locationsByCompany.TryGetValue(companyId, out var prefixes))
                {
                    prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    locationsByCompany[companyId] = prefixes;
                }

                if (string.Equals(prefix, "HO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "KB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "PK", StringComparison.OrdinalIgnoreCase))
                {
                    prefixes.Add(prefix);
                }
            }
        }

        foreach (var entry in locationsByCompany)
        {
            if (entry.Value.Count == 0)
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var desiredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prefix in entry.Value.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                var templates = PalmOilSeedAccountTemplates
                    .Where(template => string.Equals(template.Scope, "ALL", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(template.Scope, prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(template => template.Level)
                    .ThenBy(template => template.SortOrder)
                    .ToList();

                foreach (var template in templates)
                {
                    var fullCode = $"{prefix}.{template.CodeSuffix}";
                    desiredCodes.Add(fullCode);

                    long? parentAccountId = null;
                    if (!string.IsNullOrWhiteSpace(template.ParentSuffix))
                    {
                        var parentCode = $"{prefix}.{template.ParentSuffix}";
                        await using var parentLookup = new NpgsqlCommand(@"
SELECT id
FROM gl_accounts
WHERE company_id = @company_id
  AND account_code = @account_code;", connection, transaction);
                        parentLookup.Parameters.AddWithValue("company_id", entry.Key);
                        parentLookup.Parameters.AddWithValue("account_code", parentCode);
                        var scalar = await parentLookup.ExecuteScalarAsync(cancellationToken);
                        if (scalar is null || scalar is DBNull)
                        {
                            continue;
                        }

                        parentAccountId = Convert.ToInt64(scalar);
                    }

                    var isPosting = !IsSummaryAccountCode(fullCode);
                    await using var upsert = new NpgsqlCommand(@"
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
    updated_at)
VALUES (
    @company_id,
    @account_code,
    @account_name,
    @account_type,
    @normal_balance,
    @parent_account_id,
    @hierarchy_level,
    @is_posting,
    TRUE,
    @sort_order,
    @report_group,
    @cashflow_category,
    @requires_department,
    @requires_project,
    @requires_cost_center,
    'SYSTEM',
    'SYSTEM',
    NOW(),
    NOW())
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    parent_account_id = EXCLUDED.parent_account_id,
    hierarchy_level = EXCLUDED.hierarchy_level,
    is_posting = EXCLUDED.is_posting,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    cashflow_category = EXCLUDED.cashflow_category,
    requires_department = EXCLUDED.requires_department,
    requires_project = EXCLUDED.requires_project,
    requires_cost_center = EXCLUDED.requires_cost_center,
    updated_by = 'SYSTEM',
    updated_at = NOW();", connection, transaction);
                    upsert.Parameters.AddWithValue("company_id", entry.Key);
                    upsert.Parameters.AddWithValue("account_code", fullCode);
                    upsert.Parameters.AddWithValue("account_name", template.Name);
                    upsert.Parameters.AddWithValue("account_type", template.AccountType);
                    upsert.Parameters.AddWithValue("normal_balance", template.NormalBalance);
                    upsert.Parameters.AddWithValue("parent_account_id", NpgsqlDbType.Bigint, parentAccountId.HasValue ? parentAccountId.Value : DBNull.Value);
                    upsert.Parameters.AddWithValue("hierarchy_level", template.Level);
                    upsert.Parameters.AddWithValue("is_posting", isPosting);
                    upsert.Parameters.AddWithValue("sort_order", template.SortOrder);
                    upsert.Parameters.AddWithValue("report_group", template.ReportGroup);
                    upsert.Parameters.AddWithValue("cashflow_category", template.CashflowCategory);
                    upsert.Parameters.AddWithValue("requires_department", template.RequiresDepartment);
                    upsert.Parameters.AddWithValue("requires_project", template.RequiresProject);
                    upsert.Parameters.AddWithValue("requires_cost_center", template.RequiresCostCenter);
                    await upsert.ExecuteNonQueryAsync(cancellationToken);
                }

                var desiredCodeArray = desiredCodes
                    .Select(static code => code.ToUpperInvariant())
                    .ToArray();
                await using var cleanup = new NpgsqlCommand(@"
DELETE FROM gl_accounts a
WHERE a.company_id = @company_id
  AND upper(a.account_code) LIKE @prefix_pattern
  AND upper(COALESCE(a.created_by, 'SYSTEM')) IN ('SYSTEM', 'SEED')
  AND NOT (upper(a.account_code) = ANY(@desired_codes))
  AND NOT EXISTS (
      SELECT 1
      FROM gl_accounts c
      WHERE c.parent_account_id = a.id)
  AND NOT EXISTS (
      SELECT 1
      FROM gl_journal_details d
      WHERE d.account_id = a.id)
  AND NOT EXISTS (
      SELECT 1
      FROM gl_ledger_entries le
      WHERE le.account_id = a.id);", connection, transaction);
                cleanup.Parameters.AddWithValue("company_id", entry.Key);
                cleanup.Parameters.AddWithValue("prefix_pattern", $"{prefix}.%");
                cleanup.Parameters.Add(new NpgsqlParameter("desired_codes", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
                {
                    Value = desiredCodeArray
                });
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }

            await RebuildAccountHierarchyInternalAsync(
                connection,
                transaction,
                entry.Key,
                "SYSTEM",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
    }
}
