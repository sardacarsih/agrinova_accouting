using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace Accounting.Services;

public sealed class FinancialReportXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public void Export(
        string filePath,
        DateTime periodMonth,
        IReadOnlyCollection<ManagedTrialBalanceRow> trialBalanceRows,
        IReadOnlyCollection<ManagedProfitLossRow> profitLossRows,
        IReadOnlyCollection<ManagedBalanceSheetRow> balanceSheetRows)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var trialRows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Kode Akun", "Nama Akun", "Debit", "Kredit", "Saldo Bersih" }
        };
        foreach (var row in (trialBalanceRows ?? Array.Empty<ManagedTrialBalanceRow>()).OrderBy(x => x.AccountCode))
        {
            trialRows.Add(new List<object?> { row.AccountCode, row.AccountName, row.TotalDebit, row.TotalCredit, row.NetBalance });
        }

        var profitLossSheetRows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Kelompok", "Kode Akun", "Nama Akun", "Jumlah" }
        };
        foreach (var row in (profitLossRows ?? Array.Empty<ManagedProfitLossRow>())
                     .OrderBy(x => x.Section)
                     .ThenBy(x => x.AccountCode))
        {
            profitLossSheetRows.Add(new List<object?> { row.Section, row.AccountCode, row.AccountName, row.Amount });
        }

        var balanceRows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Kelompok", "Level", "Kode Akun", "Nama Akun", "Saldo" }
        };
        foreach (var row in (balanceSheetRows ?? Array.Empty<ManagedBalanceSheetRow>())
                     .OrderBy(x => x.Section)
                     .ThenBy(x => x.Level)
                     .ThenBy(x => x.AccountCode))
        {
            var indent = row.Level > 1 ? new string(' ', (row.Level - 1) * 2) : string.Empty;
            balanceRows.Add(new List<object?> { row.Section, row.Level, row.AccountCode, $"{indent}{row.AccountName}", row.Amount });
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(trialRows));
        WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildWorksheetXml(profitLossSheetRows));
        WriteEntry(archive, "xl/worksheets/sheet3.xml", BuildWorksheetXml(balanceRows));
    }

    public void ExportGeneralLedger(
        string filePath,
        DateTime periodMonth,
        IReadOnlyCollection<ManagedGeneralLedgerRow> ledgerRows,
        string accountCode = "",
        string keyword = "")
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var normalizedAccountCode = string.IsNullOrWhiteSpace(accountCode) ? "SEMUA" : accountCode.Trim().ToUpperInvariant();
        var normalizedKeyword = string.IsNullOrWhiteSpace(keyword) ? "-" : keyword.Trim();

        var rows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Akun", normalizedAccountCode },
            new() { "Keyword", normalizedKeyword },
            new()
            {
                "Tanggal",
                "No Jurnal",
                "Referensi",
                "Keterangan Jurnal",
                "Kode Akun",
                "Nama Akun",
                "Tipe Buku Bantu",
                "Kode Buku Bantu",
                "Nama Buku Bantu",
                "Keterangan Baris",
                "Debit",
                "Kredit",
                "Saldo Berjalan"
            }
        };

        foreach (var row in (ledgerRows ?? Array.Empty<ManagedGeneralLedgerRow>()))
        {
            rows.Add(new List<object?>
            {
                row.JournalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.JournalNo,
                row.ReferenceNo,
                row.JournalDescription,
                row.AccountCode,
                row.AccountName,
                row.SubledgerType,
                row.SubledgerCode,
                row.SubledgerName,
                row.LineDescription,
                row.Debit,
                row.Credit,
                row.RunningBalance
            });
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXmlSingleSheet());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXmlSingleSheet("BukuBesar"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXmlSingleSheet());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    public void ExportSubLedger(
        string filePath,
        DateTime periodMonth,
        IReadOnlyCollection<ManagedSubLedgerRow> subLedgerRows,
        string accountCode = "",
        string keyword = "")
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var normalizedAccountCode = string.IsNullOrWhiteSpace(accountCode) ? "SEMUA" : accountCode.Trim().ToUpperInvariant();
        var normalizedKeyword = string.IsNullOrWhiteSpace(keyword) ? "-" : keyword.Trim();

        var rows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Akun", normalizedAccountCode },
            new() { "Keyword", normalizedKeyword },
            new()
            {
                "Tanggal",
                "No Jurnal",
                "Referensi",
                "Keterangan Jurnal",
                "Kode Akun",
                "Nama Akun",
                "Departemen",
                "Project",
                "Tipe Buku Bantu",
                "Kode Buku Bantu",
                "Nama Buku Bantu",
                "Cost Center",
                "Estate",
                "Division",
                "Block",
                "Keterangan Baris",
                "Debit",
                "Kredit",
                "Saldo Berjalan"
            }
        };

        foreach (var row in (subLedgerRows ?? Array.Empty<ManagedSubLedgerRow>()))
        {
            rows.Add(new List<object?>
            {
                row.JournalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.JournalNo,
                row.ReferenceNo,
                row.JournalDescription,
                row.AccountCode,
                row.AccountName,
                row.DepartmentCode,
                row.ProjectCode,
                row.SubledgerType,
                row.SubledgerCode,
                row.SubledgerName,
                row.CostCenterCode,
                row.EstateCode,
                row.DivisionCode,
                row.BlockCode,
                row.LineDescription,
                row.Debit,
                row.Credit,
                row.RunningBalance
            });
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXmlSingleSheet());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXmlSingleSheet("SubLedger"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXmlSingleSheet());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    public void ExportCashFlow(
        string filePath,
        DateTime periodMonth,
        IReadOnlyCollection<ManagedCashFlowRow> cashFlowRows)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var rows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Kode Akun", "Nama Akun", "Saldo Awal", "Kas Masuk", "Kas Keluar", "Saldo Akhir" }
        };

        foreach (var row in (cashFlowRows ?? Array.Empty<ManagedCashFlowRow>())
                     .OrderBy(x => x.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new List<object?>
            {
                row.AccountCode,
                row.AccountName,
                row.OpeningBalance,
                row.CashIn,
                row.CashOut,
                row.EndingBalance
            });
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXmlSingleSheet());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXmlSingleSheet("ArusKas"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXmlSingleSheet());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    public void ExportAccountMutation(
        string filePath,
        DateTime periodMonth,
        IReadOnlyCollection<ManagedAccountMutationRow> mutationRows,
        string accountCode = "",
        string keyword = "")
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = periodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var normalizedAccountCode = string.IsNullOrWhiteSpace(accountCode) ? "SEMUA" : accountCode.Trim().ToUpperInvariant();
        var normalizedKeyword = string.IsNullOrWhiteSpace(keyword) ? "-" : keyword.Trim();

        var rows = new List<List<object?>>
        {
            new() { "Periode", periodLabel },
            new() { "Akun", normalizedAccountCode },
            new() { "Keyword", normalizedKeyword },
            new() { "Kode Akun", "Nama Akun", "Saldo Awal", "Debit Mutasi", "Kredit Mutasi", "Saldo Akhir" }
        };

        foreach (var row in (mutationRows ?? Array.Empty<ManagedAccountMutationRow>())
                     .OrderBy(x => x.AccountCode, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new List<object?>
            {
                row.AccountCode,
                row.AccountName,
                row.OpeningBalance,
                row.MutationDebit,
                row.MutationCredit,
                row.EndingBalance
            });
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXmlSingleSheet());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXmlSingleSheet("MutasiAkun"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXmlSingleSheet());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    public void ExportDashboard(
        string filePath,
        AccountingDashboardRequest request,
        AccountingDashboardData data)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var periodLabel = request.PeriodStart == default
            ? DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : request.PeriodStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var sheetNames = new[] { "Summary", "Trend", "ExpenseTop10", "GLSnapshot", "CashBank", "Inventory", "Alerts" };

        var summaryRows = new List<List<object?>>
        {
            new() { "Company", data.HeaderContext.CompanyDisplayName },
            new() { "Location", data.HeaderContext.LocationDisplayName },
            new() { "Period", periodLabel },
            new() { "Currency", data.HeaderContext.CurrencyCode },
            new() { "LastUpdated", data.LastUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) },
            new() { "KPI", "Primary", "Secondary", "DeltaPercent" }
        };
        foreach (var kpi in data.Kpis)
        {
            summaryRows.Add(new List<object?> { kpi.Label, kpi.PrimaryValue, kpi.SecondaryValue, kpi.DeltaPercent });
        }

        var trendRows = new List<List<object?>>
        {
            new() { "Period", "Revenue", "Expense" }
        };
        foreach (var point in data.RevenueExpenseTrend)
        {
            trendRows.Add(new List<object?> { point.Label, point.Revenue, point.Expense });
        }

        var expenseRows = new List<List<object?>>
        {
            new() { "AccountCode", "AccountName", "Amount" }
        };
        foreach (var point in data.TopExpenseAccounts)
        {
            expenseRows.Add(new List<object?> { point.AccountCode, point.AccountName, point.Amount });
        }

        var glRows = new List<List<object?>>
        {
            new() { "DraftCount", data.GlSnapshot.DraftCount },
            new() { "PostedCount", data.GlSnapshot.PostedCount },
            new() { "PendingPostingCount", data.GlSnapshot.PendingPostingCount },
            new() { "JournalNo", "JournalDate", "ReferenceNo", "Description", "Status", "TotalAmount" }
        };
        foreach (var journal in data.GlSnapshot.RecentTransactions)
        {
            glRows.Add(new List<object?>
            {
                journal.JournalNo,
                journal.JournalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                journal.ReferenceNo,
                journal.Description,
                journal.Status,
                journal.TotalAmount
            });
        }

        var cashRows = new List<List<object?>>
        {
            new() { "TotalBalance", data.CashBank.TotalBalance },
            new() { "TodayInflow", data.CashBank.TodayInflow },
            new() { "TodayOutflow", data.CashBank.TodayOutflow },
            new() { "PeriodInflow", data.CashBank.PeriodInflow },
            new() { "PeriodOutflow", data.CashBank.PeriodOutflow },
            new() { "AccountCode", "AccountName", "EndingBalance" }
        };
        foreach (var item in data.CashBank.Accounts)
        {
            cashRows.Add(new List<object?> { item.AccountCode, item.AccountName, item.EndingBalance });
        }

        var inventoryRows = new List<List<object?>>
        {
            new() { "TotalValue", data.Inventory.TotalValue },
            new() { "LowStockCount", data.Inventory.LowStockCount },
            new() { "TopMovingItem", "Name", "Uom", "Qty" }
        };
        foreach (var item in data.Inventory.TopMovingItems)
        {
            inventoryRows.Add(new List<object?> { item.ItemCode, item.ItemName, item.Uom, item.Qty });
        }

        inventoryRows.Add(new List<object?> { "LowStockItem", "Name", "Location", "Qty" });
        foreach (var item in data.Inventory.LowStockItems)
        {
            inventoryRows.Add(new List<object?> { item.ItemCode, item.ItemName, item.LocationName, item.Qty });
        }

        var alertRows = new List<List<object?>>
        {
            new() { "Title", "Severity", "Count", "Message", "ActionLabel" }
        };
        foreach (var alert in data.Alerts)
        {
            alertRows.Add(new List<object?> { alert.Title, alert.Severity.ToString(), alert.Count, alert.Message, alert.ActionLabel });
        }

        var sheetRows = new[]
        {
            summaryRows,
            trendRows,
            expenseRows,
            glRows,
            cashRows,
            inventoryRows,
            alertRows
        };

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml(sheetNames.Length));
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheetNames));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml(sheetNames.Length));
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());

        for (var index = 0; index < sheetRows.Length; index++)
        {
            WriteEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheetXml(sheetRows[index]));
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private static XDocument BuildContentTypesXml()
    {
        return BuildContentTypesXml(3);
    }

    private static XDocument BuildContentTypesXml(int sheetCount)
    {
        XNamespace typesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = new XElement(typesNs + "Types",
            new XElement(typesNs + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(typesNs + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            new XElement(typesNs + "Override",
                new XAttribute("PartName", "/xl/workbook.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));

        for (var index = 1; index <= sheetCount; index++)
        {
            types.Add(new XElement(typesNs + "Override",
                new XAttribute("PartName", $"/xl/worksheets/sheet{index}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        }

        types.Add(new XElement(typesNs + "Override",
            new XAttribute("PartName", "/xl/styles.xml"),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));

        return new XDocument(types);
    }

    private static XDocument BuildContentTypesXmlSingleSheet()
    {
        return BuildContentTypesXml(1);
    }

    private static XDocument BuildWorkbookXml()
    {
        return BuildWorkbookXml(["NeracaSaldo", "LabaRugi", "Neraca"]);
    }

    private static XDocument BuildWorkbookXml(IEnumerable<string> sheetNames)
    {
        var sheetElements = new List<XElement>();
        var index = 1;
        foreach (var sheetName in sheetNames)
        {
            sheetElements.Add(new XElement(SpreadsheetNs + "sheet",
                new XAttribute("name", string.IsNullOrWhiteSpace(sheetName) ? $"Sheet{index}" : sheetName.Trim()),
                new XAttribute("sheetId", index.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(RelNs + "id", $"rId{index}")));
            index++;
        }

        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets", sheetElements)));
    }

    private static XDocument BuildRootRelsXml()
    {
        return new XDocument(
            new XElement(PackageRelNs + "Relationships",
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static XDocument BuildWorkbookXmlSingleSheet(string sheetName)
    {
        var normalizedSheetName = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim();
        return BuildWorkbookXml([normalizedSheetName]);
    }

    private static XDocument BuildWorkbookRelsXml()
    {
        return BuildWorkbookRelsXml(3);
    }

    private static XDocument BuildWorkbookRelsXml(int sheetCount)
    {
        var relationships = new XElement(PackageRelNs + "Relationships");
        for (var index = 1; index <= sheetCount; index++)
        {
            relationships.Add(new XElement(PackageRelNs + "Relationship",
                new XAttribute("Id", $"rId{index}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", $"worksheets/sheet{index}.xml")));
        }

        relationships.Add(new XElement(PackageRelNs + "Relationship",
            new XAttribute("Id", $"rId{sheetCount + 1}"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
            new XAttribute("Target", "styles.xml")));
        return new XDocument(relationships);
    }

    private static XDocument BuildWorkbookRelsXmlSingleSheet()
    {
        return BuildWorkbookRelsXml(1);
    }

    private static XDocument BuildStylesXml()
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "styleSheet",
                new XElement(SpreadsheetNs + "fonts", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "font",
                        new XElement(SpreadsheetNs + "sz", new XAttribute("val", "11")),
                        new XElement(SpreadsheetNs + "name", new XAttribute("val", "Calibri")))),
                new XElement(SpreadsheetNs + "fills", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "fill", new XElement(SpreadsheetNs + "patternFill", new XAttribute("patternType", "none")))),
                new XElement(SpreadsheetNs + "borders", new XAttribute("count", "1"), new XElement(SpreadsheetNs + "border")),
                new XElement(SpreadsheetNs + "cellStyleXfs", new XAttribute("count", "1"), new XElement(SpreadsheetNs + "xf")),
                new XElement(SpreadsheetNs + "cellXfs", new XAttribute("count", "1"), new XElement(SpreadsheetNs + "xf", new XAttribute("xfId", "0")))));
    }

    private static XDocument BuildWorksheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sheetData = new XElement(SpreadsheetNs + "sheetData");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowIndex + 1));
            var values = rows[rowIndex];
            for (var colIndex = 0; colIndex < values.Count; colIndex++)
            {
                var cellRef = GetCellReference(colIndex, rowIndex + 1);
                var value = values[colIndex];

                if (value is null)
                {
                    continue;
                }

                if (value is decimal or double or float or int or long)
                {
                    row.Add(new XElement(SpreadsheetNs + "c",
                        new XAttribute("r", cellRef),
                        new XElement(SpreadsheetNs + "v", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0")));
                }
                else
                {
                    row.Add(new XElement(SpreadsheetNs + "c",
                        new XAttribute("r", cellRef),
                        new XAttribute("t", "inlineStr"),
                        new XElement(SpreadsheetNs + "is",
                            new XElement(SpreadsheetNs + "t", value.ToString() ?? string.Empty))));
                }
            }

            sheetData.Add(row);
        }

        return new XDocument(new XElement(SpreadsheetNs + "worksheet", sheetData));
    }

    private static string GetCellReference(int columnIndex, int rowIndex)
    {
        var dividend = columnIndex + 1;
        var column = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            column = Convert.ToChar(65 + modulo) + column;
            dividend = (dividend - modulo) / 26;
        }

        return $"{column}{rowIndex}";
    }
}

