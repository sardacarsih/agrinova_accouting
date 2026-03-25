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
                "Cost Center",
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
                row.CostCenterCode,
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

    private static void WriteEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private static XDocument BuildContentTypesXml()
    {
        XNamespace typesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new XDocument(
            new XElement(typesNs + "Types",
                new XElement(typesNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(typesNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet2.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet3.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))));
    }

    private static XDocument BuildContentTypesXmlSingleSheet()
    {
        XNamespace typesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new XDocument(
            new XElement(typesNs + "Types",
                new XElement(typesNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(typesNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(typesNs + "Override",
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))));
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

    private static XDocument BuildWorkbookXml()
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "NeracaSaldo"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")),
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "LabaRugi"),
                        new XAttribute("sheetId", "2"),
                        new XAttribute(RelNs + "id", "rId2")),
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "Neraca"),
                        new XAttribute("sheetId", "3"),
                        new XAttribute(RelNs + "id", "rId3")))));
    }

    private static XDocument BuildWorkbookXmlSingleSheet(string sheetName)
    {
        var normalizedSheetName = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim();
        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", normalizedSheetName),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")))));
    }

    private static XDocument BuildWorkbookRelsXml()
    {
        return new XDocument(
            new XElement(PackageRelNs + "Relationships",
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet2.xml")),
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId3"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet3.xml")),
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId4"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))));
    }

    private static XDocument BuildWorkbookRelsXmlSingleSheet()
    {
        return new XDocument(
            new XElement(PackageRelNs + "Relationships",
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))));
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

