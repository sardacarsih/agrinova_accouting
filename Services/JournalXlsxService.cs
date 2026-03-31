using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class JournalXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public void Export(string filePath, ManagedJournalHeader header, IReadOnlyCollection<ManagedJournalLine> lines)
    {
        var singleBundle = new ManagedJournalBundle
        {
            Header = header ?? new ManagedJournalHeader(),
            Lines = (lines ?? Array.Empty<ManagedJournalLine>()).ToList()
        };

        ExportMany(filePath, new[] { singleBundle });
    }

    public void ExportMany(string filePath, IReadOnlyCollection<ManagedJournalBundle> journals)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        var sourceJournals = journals ?? Array.Empty<ManagedJournalBundle>();
        if (sourceJournals.Count == 0)
        {
            throw new InvalidOperationException("Tidak ada jurnal terpilih untuk diexport.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var rows = new List<List<object?>>
        {
            new()
            {
                "JournalNo",
                "JournalDate",
                "PeriodMonth",
                "ReferenceNo",
                "JournalDescription",
                "JournalStatus",
                "LineNo",
                "AccountCode",
                "LineDescription",
                "Debit",
                "Credit",
                "DepartmentCode",
                "ProjectCode",
                "SubledgerCode",
                "CostCenterCode"
            }
        };

        var orderedJournals = sourceJournals
            .Where(x => x is not null)
            .OrderBy(x => x.Header.JournalNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Header.JournalDate)
            .ToList();

        foreach (var journal in orderedJournals)
        {
            var header = journal.Header ?? new ManagedJournalHeader();
            var lines = (journal.Lines ?? new List<ManagedJournalLine>())
                .OrderBy(x => x.LineNo)
                .ToList();

            foreach (var line in lines)
            {
                rows.Add(new List<object?>
                {
                    header.JournalNo,
                    header.JournalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ResolvePeriodMonth(header).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    header.ReferenceNo,
                    header.Description,
                    header.Status,
                    line.LineNo,
                    line.AccountCode,
                    line.Description,
                    line.Debit,
                    line.Credit,
                    line.DepartmentCode,
                    line.ProjectCode,
                    line.SubledgerCode,
                    line.CostCenterCode
                });
            }
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildSingleSheetContentTypesXml());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildSingleSheetWorkbookXml("Journals"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildSingleSheetWorkbookRelsXml());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    public void ExportManyLegacy(string filePath, IReadOnlyCollection<ManagedJournalBundle> journals)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        var sourceJournals = journals ?? Array.Empty<ManagedJournalBundle>();
        if (sourceJournals.Count == 0)
        {
            throw new InvalidOperationException("Tidak ada jurnal terpilih untuk diexport.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var headerRows = new List<List<object?>>
        {
            new()
            {
                "JournalNo",
                "JournalDate",
                "PeriodMonth",
                "ReferenceNo",
                "Description",
                "Status"
            }
        };

        var detailRows = new List<List<object?>>
        {
            new()
            {
                "JournalNo",
                "LineNo",
                "AccountCode",
                "Description",
                "Debit",
                "Credit",
                "DepartmentCode",
                "ProjectCode",
                "SubledgerCode",
                "CostCenterCode"
            }
        };

        var orderedJournals = sourceJournals
            .Where(x => x is not null)
            .OrderBy(x => x.Header.JournalNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Header.JournalDate)
            .ToList();

        foreach (var journal in orderedJournals)
        {
            var header = journal.Header ?? new ManagedJournalHeader();
            var lines = (journal.Lines ?? new List<ManagedJournalLine>())
                .OrderBy(x => x.LineNo)
                .ToList();

            headerRows.Add(new List<object?>
            {
                header.JournalNo,
                header.JournalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ResolvePeriodMonth(header).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                header.ReferenceNo,
                header.Description,
                header.Status
            });

            foreach (var line in lines)
            {
                detailRows.Add(new List<object?>
                {
                    header.JournalNo,
                    line.LineNo,
                    line.AccountCode,
                    line.Description,
                    line.Debit,
                    line.Credit,
                    line.DepartmentCode,
                    line.ProjectCode,
                    line.SubledgerCode,
                    line.CostCenterCode
                });
            }
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildTwoSheetContentTypesXml());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildTwoSheetWorkbookXml("Header", "Detail"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildTwoSheetWorkbookRelsXml());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(headerRows));
        WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildWorksheetXml(detailRows));
    }

    public JournalImportLoadResult Import(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = "File import tidak ditemukan."
            };
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);

            var workbook = LoadXDocument(archive, "xl/workbook.xml");
            var workbookRels = LoadXDocument(archive, "xl/_rels/workbook.xml.rels");
            var sharedStrings = TryLoadSharedStrings(archive);

            var sheetTargets = ResolveSheetTargets(workbook, workbookRels);
            if (sheetTargets.TryGetValue("Journals", out var journalsPath))
            {
                var journalRows = ReadRows(LoadXDocument(archive, journalsPath), sharedStrings);
                return ImportFromFlatJournalsSheet(journalRows);
            }

            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = "Format import tidak didukung. Gunakan 1 sheet bernama 'Journals'."
            };
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(JournalXlsxService),
                "ImportXlsxFailed",
                $"action=import_xlsx file_path={filePath}",
                ex);
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = $"Gagal membaca file XLSX: {ex.Message}"
            };
        }
    }

    private static JournalImportLoadResult ImportFromFlatJournalsSheet(IReadOnlyList<Dictionary<int, string>> journalRows)
    {
        if (journalRows.Count <= 1)
        {
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = "Sheet Journals kosong atau tidak memiliki data."
            };
        }

        var headerRow = journalRows[0];
        var columnByKey = BuildFlatColumnMap(headerRow);
        if (!columnByKey.TryGetValue("JOURNALNO", out var journalNoCol) ||
            !columnByKey.TryGetValue("ACCOUNTCODE", out var accountCodeCol) ||
            !columnByKey.TryGetValue("DEBIT", out var debitCol) ||
            !columnByKey.TryGetValue("CREDIT", out var creditCol))
        {
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = "Kolom wajib untuk format Journals tidak lengkap (JournalNo, AccountCode, Debit, Credit)."
            };
        }

        var journalDateCol = GetColumnIndex(columnByKey, "JOURNALDATE");
        var periodMonthCol = GetColumnIndex(columnByKey, "PERIODMONTH");
        var referenceNoCol = GetColumnIndex(columnByKey, "REFERENCENO");
        var journalDescriptionCol = GetColumnIndex(columnByKey, "JOURNALDESCRIPTION");
        var journalStatusCol = GetColumnIndex(columnByKey, "JOURNALSTATUS");
        var lineNoCol = GetColumnIndex(columnByKey, "LINENO");
        var lineDescriptionCol = GetColumnIndex(columnByKey, "LINEDESCRIPTION");
        var genericDescriptionCol = GetColumnIndex(columnByKey, "DESCRIPTION");
        var departmentCol = GetColumnIndex(columnByKey, "DEPARTMENTCODE");
        var projectCol = GetColumnIndex(columnByKey, "PROJECTCODE");
        var subledgerCol = GetColumnIndex(columnByKey, "SUBLEDGERCODE");
        var costCenterCol = GetColumnIndex(columnByKey, "COSTCENTERCODE");

        var groups = new Dictionary<string, JournalImportGroup>(StringComparer.OrdinalIgnoreCase);
        var previewBuffer = new List<JournalImportPreviewBuffer>();
        for (var i = 1; i < journalRows.Count; i++)
        {
            var row = journalRows[i];
            var rowNumber = i + 1;

            var journalNo = GetCell(row, journalNoCol);
            var accountCode = GetCell(row, accountCodeCol);
            var lineDescription = GetCell(row, lineDescriptionCol >= 0 ? lineDescriptionCol : genericDescriptionCol);
            var journalDescription = GetCell(row, journalDescriptionCol >= 0 ? journalDescriptionCol : -1);
            var referenceNo = GetCell(row, referenceNoCol);
            var journalStatus = GetCell(row, journalStatusCol);
            var deptCode = GetCell(row, departmentCol);
            var projectCode = GetCell(row, projectCol);
            var subledgerCode = GetCell(row, subledgerCol);
            var costCenterCode = GetCell(row, costCenterCol);

            var debit = ParseDecimal(GetCell(row, debitCol));
            var credit = ParseDecimal(GetCell(row, creditCol));
            var lineNo = ParseInt(GetCell(row, lineNoCol));
            if (lineNo <= 0 && groups.TryGetValue(journalNo, out var existingGroup))
            {
                lineNo = existingGroup.Lines.Count + 1;
            }
            else if (lineNo <= 0)
            {
                lineNo = 1;
            }

            var isValid = true;
            var message = string.Empty;
            if (string.IsNullOrWhiteSpace(journalNo))
            {
                isValid = false;
                message = "JournalNo wajib diisi.";
            }
            else if (string.IsNullOrWhiteSpace(accountCode))
            {
                isValid = false;
                message = "AccountCode wajib diisi.";
            }
            else if (debit < 0 || credit < 0 || (debit > 0 && credit > 0) || (debit == 0 && credit == 0))
            {
                isValid = false;
                message = "Debit/Kredit tidak valid.";
            }

            var previewRow = new JournalImportPreviewBuffer
            {
                RowNumber = rowNumber,
                LineNo = lineNo,
                JournalNo = journalNo,
                AccountCode = accountCode,
                Description = lineDescription,
                Debit = debit,
                Credit = credit,
                DepartmentCode = deptCode,
                ProjectCode = projectCode,
                SubledgerCode = subledgerCode,
                CostCenterCode = costCenterCode,
                IsValid = isValid,
                ValidationMessage = message
            };
            previewBuffer.Add(previewRow);

            if (!isValid)
            {
                continue;
            }

            if (!groups.TryGetValue(journalNo, out var group))
            {
                var journalDate = DateTime.Today;
                if (journalDateCol >= 0)
                {
                    var dateValue = GetCell(row, journalDateCol);
                    if (DateTime.TryParse(dateValue, out var parsedDate))
                    {
                        journalDate = parsedDate.Date;
                    }
                }

                var periodMonth = new DateTime(journalDate.Year, journalDate.Month, 1);
                if (periodMonthCol >= 0)
                {
                    var periodValue = GetCell(row, periodMonthCol);
                    if (DateTime.TryParse(periodValue, out var parsedPeriod))
                    {
                        periodMonth = new DateTime(parsedPeriod.Year, parsedPeriod.Month, 1);
                    }
                }

                group = new JournalImportGroup
                {
                    Header = new ManagedJournalHeader
                    {
                        JournalNo = journalNo,
                        JournalDate = journalDate,
                        PeriodMonth = periodMonth,
                        ReferenceNo = referenceNo,
                        Description = journalDescription,
                        Status = string.IsNullOrWhiteSpace(journalStatus) ? "DRAFT" : journalStatus
                    }
                };
                groups[journalNo] = group;
            }

            group.Lines.Add(new ManagedJournalLine
            {
                LineNo = lineNo,
                AccountCode = accountCode.ToUpperInvariant(),
                Description = lineDescription,
                Debit = debit,
                Credit = credit,
                DepartmentCode = deptCode,
                ProjectCode = projectCode,
                SubledgerCode = subledgerCode,
                CostCenterCode = costCenterCode
            });
        }

        var bundleResults = new List<JournalImportBundleResult>();
        foreach (var entry in groups.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var header = entry.Value.Header;
            var lines = entry.Value.Lines
                .OrderBy(x => x.LineNo)
                .Select((x, idx) => new ManagedJournalLine
                {
                    LineNo = idx + 1,
                    AccountCode = x.AccountCode,
                    AccountName = x.AccountName,
                    Description = x.Description,
                    Debit = x.Debit,
                    Credit = x.Credit,
                    DepartmentCode = x.DepartmentCode,
                    ProjectCode = x.ProjectCode,
                    SubledgerCode = x.SubledgerCode,
                    CostCenterCode = x.CostCenterCode
                })
                .ToList();

            var validationMessage = string.Empty;
            var isValid = true;
            if (lines.Count == 0)
            {
                isValid = false;
                validationMessage = $"Jurnal {header.JournalNo} tidak memiliki detail valid.";
            }
            else
            {
                var totalDebit = lines.Sum(x => x.Debit);
                var totalCredit = lines.Sum(x => x.Credit);
                if (totalDebit != totalCredit)
                {
                    isValid = false;
                    validationMessage = $"Jurnal {header.JournalNo} tidak seimbang (debit != kredit).";
                }
            }

            if (!isValid)
            {
                for (var index = 0; index < previewBuffer.Count; index++)
                {
                    if (!string.Equals(previewBuffer[index].JournalNo, header.JournalNo, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!previewBuffer[index].IsValid)
                    {
                        continue;
                    }

                    previewBuffer[index].IsValid = false;
                    previewBuffer[index].ValidationMessage = validationMessage;
                }
            }

            bundleResults.Add(new JournalImportBundleResult
            {
                Header = header,
                Lines = lines,
                IsValid = isValid,
                ValidationMessage = validationMessage
            });
        }

        var previewItems = previewBuffer
            .OrderBy(x => x.RowNumber)
            .Select(x => new JournalImportPreviewItem
            {
                RowNumber = x.RowNumber,
                LineNo = x.LineNo,
                JournalNo = x.JournalNo,
                AccountCode = x.AccountCode,
                AccountName = string.Empty,
                Description = x.Description,
                Debit = x.Debit,
                Credit = x.Credit,
                DepartmentCode = x.DepartmentCode,
                ProjectCode = x.ProjectCode,
                SubledgerCode = x.SubledgerCode,
                CostCenterCode = x.CostCenterCode,
                IsValid = x.IsValid,
                ValidationMessage = x.ValidationMessage
            })
            .ToList();

        var validBundles = bundleResults.Where(x => x.IsValid).ToList();
        var invalidBundles = bundleResults.Count - validBundles.Count;

        if (validBundles.Count == 0)
        {
            return new JournalImportLoadResult
            {
                IsSuccess = false,
                Message = "Tidak ada jurnal valid untuk diimport.",
                JournalBundles = bundleResults,
                PreviewItems = previewItems
            };
        }

        return new JournalImportLoadResult
        {
            IsSuccess = true,
            Message = invalidBundles > 0
                ? $"Preview selesai: {validBundles.Count} jurnal valid, {invalidBundles} jurnal invalid."
                : $"Preview selesai: {validBundles.Count} jurnal valid.",
            JournalBundles = bundleResults,
            PreviewItems = previewItems
        };
    }

    private static Dictionary<string, int> BuildFlatColumnMap(IReadOnlyDictionary<int, string> headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headerRow)
        {
            var normalized = NormalizeFlatColumnName(pair.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            map[normalized] = pair.Key;
        }

        return map;
    }

    private static DateTime ResolvePeriodMonth(ManagedJournalHeader header)
    {
        var fallbackDate = header.JournalDate == default ? DateTime.Today : header.JournalDate;
        var source = header.PeriodMonth == default ? fallbackDate : header.PeriodMonth;
        return new DateTime(source.Year, source.Month, 1);
    }

    private static string NormalizeFlatColumnName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return new string(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int columnIndex)
    {
        if (columnIndex < 0)
        {
            return string.Empty;
        }

        return row.TryGetValue(columnIndex, out var value) ? value.Trim() : string.Empty;
    }

    private static int GetColumnIndex(IReadOnlyDictionary<string, int> columnByKey, string key)
    {
        return columnByKey.TryGetValue(key, out var index) ? index : -1;
    }

    private static int ParseInt(string? text)
    {
        return int.TryParse(text, out var number) ? number : 0;
    }

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return Math.Round(number, 2);
        }

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out number))
        {
            return Math.Round(number, 2);
        }

        return 0m;
    }

    private static Dictionary<string, string> ResolveSheetTargets(XDocument workbook, XDocument workbookRels)
    {
        var ridToTarget = workbookRels
            .Descendants(PackageRelNs + "Relationship")
            .Where(x => x.Attribute("Id") is not null && x.Attribute("Target") is not null)
            .ToDictionary(
                x => x.Attribute("Id")!.Value,
                x => x.Attribute("Target")!.Value,
                StringComparer.OrdinalIgnoreCase);

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants(SpreadsheetNs + "sheet"))
        {
            var sheetName = sheet.Attribute("name")?.Value;
            var rid = sheet.Attribute(RelNs + "id")?.Value;
            if (string.IsNullOrWhiteSpace(sheetName) || string.IsNullOrWhiteSpace(rid))
            {
                continue;
            }

            if (!ridToTarget.TryGetValue(rid, out var target))
            {
                continue;
            }

            var normalized = target.StartsWith("/", StringComparison.Ordinal)
                ? target.TrimStart('/')
                : $"xl/{target.TrimStart('/')}";
            output[sheetName] = normalized;
        }

        return output;
    }

    private static Dictionary<int, string> TryLoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        var output = new Dictionary<int, string>();
        if (entry is null)
        {
            return output;
        }

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var index = 0;
        foreach (var item in doc.Descendants(SpreadsheetNs + "si"))
        {
            var text = string.Concat(item.Descendants(SpreadsheetNs + "t").Select(x => x.Value));
            output[index++] = text;
        }

        return output;
    }

    private static List<Dictionary<int, string>> ReadRows(XDocument sheetDocument, IReadOnlyDictionary<int, string> sharedStrings)
    {
        var rows = new List<Dictionary<int, string>>();
        var rowElements = sheetDocument.Descendants(SpreadsheetNs + "row").ToList();
        foreach (var row in rowElements)
        {
            var rowMap = new Dictionary<int, string>();
            foreach (var cell in row.Elements(SpreadsheetNs + "c"))
            {
                var reference = cell.Attribute("r")?.Value;
                var columnIndex = ExtractColumnIndex(reference);
                if (columnIndex < 0)
                {
                    continue;
                }

                var type = cell.Attribute("t")?.Value;
                string value;
                if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
                {
                    value = cell.Descendants(SpreadsheetNs + "t").FirstOrDefault()?.Value ?? string.Empty;
                }
                else if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
                {
                    var keyText = cell.Element(SpreadsheetNs + "v")?.Value;
                    if (int.TryParse(keyText, out var key) && sharedStrings.TryGetValue(key, out var sharedValue))
                    {
                        value = sharedValue;
                    }
                    else
                    {
                        value = string.Empty;
                    }
                }
                else
                {
                    value = cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
                }

                rowMap[columnIndex] = value;
            }

            rows.Add(rowMap);
        }

        return rows;
    }

    private static int ExtractColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var col = 0;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch))
            {
                break;
            }

            col = (col * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return col > 0 ? col - 1 : -1;
    }

    private static XDocument LoadXDocument(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            throw new InvalidOperationException($"Entry XLSX tidak ditemukan: {path}");
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void WriteEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private static XDocument BuildSingleSheetContentTypesXml()
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

    private static XDocument BuildTwoSheetContentTypesXml()
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

    private static XDocument BuildSingleSheetWorkbookXml(string sheetName)
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", sheetName),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")))));
    }

    private static XDocument BuildTwoSheetWorkbookXml(string sheetOneName, string sheetTwoName)
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", sheetOneName),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")),
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", sheetTwoName),
                        new XAttribute("sheetId", "2"),
                        new XAttribute(RelNs + "id", "rId2")))));
    }

    private static XDocument BuildSingleSheetWorkbookRelsXml()
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

    private static XDocument BuildTwoSheetWorkbookRelsXml()
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

        return new XDocument(
            new XElement(SpreadsheetNs + "worksheet", sheetData));
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

    private sealed class JournalImportGroup
    {
        public ManagedJournalHeader Header { get; init; } = new();

        public List<ManagedJournalLine> Lines { get; } = new();
    }

    private sealed class JournalImportPreviewBuffer
    {
        public int RowNumber { get; init; }

        public int LineNo { get; init; }

        public string JournalNo { get; init; } = string.Empty;

        public string AccountCode { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public decimal Debit { get; init; }

        public decimal Credit { get; init; }

        public string DepartmentCode { get; init; } = string.Empty;

        public string ProjectCode { get; init; } = string.Empty;

        public string SubledgerCode { get; init; } = string.Empty;

        public string CostCenterCode { get; init; } = string.Empty;

        public bool IsValid { get; set; }

        public string ValidationMessage { get; set; } = string.Empty;
    }
}

