using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class InventoryOpeningBalanceXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public AccessOperationResult CreateTemplate(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AccessOperationResult(false, "Path template saldo awal tidak valid.");
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var rows = new List<List<object?>>
            {
                new() { "CompanyCode", "LocationCode", "WarehouseCode", "ItemCode", "Qty", "UnitCost", "CutoffDate", "ReferenceNo", "Notes" },
                new() { "AGRINOVA", "HO", "GDG-UTAMA", "RAW-001", 1250m, 10000m, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "OB-20260331", "Saldo awal cutover" }
            };

            using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", BuildSingleSheetContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteEntry(archive, "xl/workbook.xml", BuildSingleSheetWorkbookXml("OpeningBalance"));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildSingleSheetWorkbookRelsXml());
            WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));

            return new AccessOperationResult(true, "Template saldo awal inventory berhasil dibuat.");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryOpeningBalanceXlsxService),
                "CreateOpeningBalanceTemplateFailed",
                $"action=create_opening_balance_template file_path={filePath}",
                ex);
            return new AccessOperationResult(false, $"Gagal membuat template saldo awal: {ex.Message}");
        }
    }

    public InventoryOpeningBalanceParseResult Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new InventoryOpeningBalanceParseResult
            {
                IsSuccess = false,
                Message = "File import saldo awal tidak ditemukan."
            };
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var workbook = LoadXDocument(archive, "xl/workbook.xml");
            var workbookRels = LoadXDocument(archive, "xl/_rels/workbook.xml.rels");
            var sharedStrings = TryLoadSharedStrings(archive);
            var sheetTargets = ResolveSheetTargets(workbook, workbookRels);

            if (!sheetTargets.TryGetValue("OpeningBalance", out var sheetPath))
            {
                return new InventoryOpeningBalanceParseResult
                {
                    IsSuccess = false,
                    Message = "Format import tidak valid. Gunakan sheet 'OpeningBalance'."
                };
            }

            var rows = ReadRows(LoadXDocument(archive, sheetPath), sharedStrings);
            var errors = new List<InventoryImportError>();
            var parsedRows = ParseRows(rows, errors);
            if (parsedRows.Count == 0)
            {
                return new InventoryOpeningBalanceParseResult
                {
                    IsSuccess = false,
                    Message = "Tidak ada data saldo awal yang valid untuk diproses.",
                    Errors = errors
                };
            }

            if (errors.Count > 0)
            {
                return new InventoryOpeningBalanceParseResult
                {
                    IsSuccess = false,
                    Message = BuildValidationErrorMessage(errors),
                    Bundle = new InventoryOpeningBalanceBundle { Rows = parsedRows },
                    Errors = errors
                };
            }

            return new InventoryOpeningBalanceParseResult
            {
                IsSuccess = true,
                Message = $"File saldo awal valid. Baris siap proses: {parsedRows.Count}.",
                Bundle = new InventoryOpeningBalanceBundle { Rows = parsedRows }
            };
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryOpeningBalanceXlsxService),
                "ParseOpeningBalanceXlsxFailed",
                $"action=parse_opening_balance_xlsx file_path={filePath}",
                ex);
            return new InventoryOpeningBalanceParseResult
            {
                IsSuccess = false,
                Message = $"Gagal membaca file XLSX saldo awal: {ex.Message}"
            };
        }
    }

    private static List<InventoryOpeningBalanceRow> ParseRows(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<InventoryOpeningBalanceRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "OpeningBalance",
                RowNumber = 0,
                Message = "Sheet OpeningBalance kosong."
            });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("LOCATIONCODE", out var locationCodeCol) ||
            !columnMap.TryGetValue("WAREHOUSECODE", out var warehouseCodeCol) ||
            !columnMap.TryGetValue("ITEMCODE", out var itemCodeCol) ||
            !columnMap.TryGetValue("QTY", out var qtyCol) ||
            !columnMap.TryGetValue("UNITCOST", out var unitCostCol) ||
            !columnMap.TryGetValue("CUTOFFDATE", out var cutoffDateCol))
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "OpeningBalance",
                RowNumber = 1,
                Message = "Kolom wajib: LocationCode, WarehouseCode, ItemCode, Qty, UnitCost, CutoffDate."
            });
            return output;
        }

        var companyCodeCol = GetColumnIndex(columnMap, "COMPANYCODE");
        var referenceNoCol = GetFirstColumnIndex(columnMap, "REFERENCENO", "REFERENCE");
        var notesCol = GetColumnIndex(columnMap, "NOTES");
        var uniqueLocationWarehouseItemPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;

            var companyCode = GetCell(row, companyCodeCol).ToUpperInvariant();
            var locationCode = GetCell(row, locationCodeCol).ToUpperInvariant();
            var warehouseCode = GetCell(row, warehouseCodeCol).ToUpperInvariant();
            var itemCode = GetCell(row, itemCodeCol).ToUpperInvariant();
            var qtyText = GetCell(row, qtyCol);
            var unitCostText = GetCell(row, unitCostCol);
            var cutoffDateText = GetCell(row, cutoffDateCol);
            var referenceNo = GetCell(row, referenceNoCol).ToUpperInvariant();
            var notes = GetCell(row, notesCol);

            if (string.IsNullOrWhiteSpace(companyCode) &&
                string.IsNullOrWhiteSpace(locationCode) &&
                string.IsNullOrWhiteSpace(warehouseCode) &&
                string.IsNullOrWhiteSpace(itemCode) &&
                string.IsNullOrWhiteSpace(qtyText) &&
                string.IsNullOrWhiteSpace(unitCostText) &&
                string.IsNullOrWhiteSpace(cutoffDateText) &&
                string.IsNullOrWhiteSpace(referenceNo) &&
                string.IsNullOrWhiteSpace(notes))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(locationCode) ||
                string.IsNullOrWhiteSpace(warehouseCode) ||
                string.IsNullOrWhiteSpace(itemCode))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "OpeningBalance",
                    RowNumber = rowNumber,
                    Message = "LocationCode, WarehouseCode, dan ItemCode wajib diisi."
                });
                continue;
            }

            if (!TryParseDecimal(qtyText, out var qty) || qty <= 0)
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "OpeningBalance",
                    RowNumber = rowNumber,
                    Message = "Qty harus angka dan lebih besar dari 0."
                });
                continue;
            }

            if (!TryParseDecimal(unitCostText, out var unitCost) || unitCost < 0)
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "OpeningBalance",
                    RowNumber = rowNumber,
                    Message = "UnitCost harus angka dan tidak boleh negatif."
                });
                continue;
            }

            if (!TryParseDate(cutoffDateText, out var cutoffDate))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "OpeningBalance",
                    RowNumber = rowNumber,
                    Message = "CutoffDate tidak valid. Gunakan format tanggal (contoh: 2026-03-31)."
                });
                continue;
            }

            var duplicateKey = $"{locationCode}|{warehouseCode}|{itemCode}";
            if (!uniqueLocationWarehouseItemPairs.Add(duplicateKey))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "OpeningBalance",
                    RowNumber = rowNumber,
                    Message = $"Duplikat item pada lokasi/gudang yang sama: {locationCode}/{warehouseCode}/{itemCode}."
                });
                continue;
            }

            output.Add(new InventoryOpeningBalanceRow
            {
                RowNumber = rowNumber,
                CompanyCode = companyCode,
                LocationCode = locationCode,
                WarehouseCode = warehouseCode,
                ItemCode = itemCode,
                Qty = Math.Round(qty, 4),
                UnitCost = Math.Round(unitCost, 4),
                CutoffDate = cutoffDate.Date,
                ReferenceNo = referenceNo,
                Notes = notes
            });
        }

        return output;
    }

    private static bool TryParseDecimal(string text, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryParseDate(string text, out DateTime value)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value))
        {
            value = value.Date;
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out value))
        {
            value = value.Date;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) ||
            double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out serial))
        {
            try
            {
                value = DateTime.FromOADate(serial).Date;
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }

        value = default;
        return false;
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyDictionary<int, string> headerRow)
    {
        var output = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headerRow)
        {
            var normalized = NormalizeColumnName(pair.Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            output[normalized] = pair.Key;
        }

        return output;
    }

    private static string NormalizeColumnName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return new string(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static int GetColumnIndex(IReadOnlyDictionary<string, int> columnMap, string key)
    {
        return columnMap.TryGetValue(key, out var index) ? index : -1;
    }

    private static int GetFirstColumnIndex(IReadOnlyDictionary<string, int> columnMap, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (columnMap.TryGetValue(key, out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int columnIndex)
    {
        if (columnIndex < 0)
        {
            return string.Empty;
        }

        return row.TryGetValue(columnIndex, out var value) ? value.Trim() : string.Empty;
    }

    private static string BuildValidationErrorMessage(IReadOnlyCollection<InventoryImportError> errors)
    {
        var preview = errors
            .Take(5)
            .Select(x => $"{x.SheetName} r{(x.RowNumber <= 0 ? 1 : x.RowNumber)}: {x.Message}")
            .ToList();

        return $"Validasi import saldo awal gagal ({errors.Count} error). {string.Join(" | ", preview)}";
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
        foreach (var row in sheetDocument.Descendants(SpreadsheetNs + "row"))
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

        var column = 0;
        foreach (var ch in cellReference)
        {
            if (!char.IsLetter(ch))
            {
                break;
            }

            column = (column * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return column > 0 ? column - 1 : -1;
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

    private static XDocument BuildStylesXml()
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "styleSheet",
                new XElement(SpreadsheetNs + "fonts",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "font",
                        new XElement(SpreadsheetNs + "sz", new XAttribute("val", "11")),
                        new XElement(SpreadsheetNs + "name", new XAttribute("val", "Calibri")))),
                new XElement(SpreadsheetNs + "fills",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "fill",
                        new XElement(SpreadsheetNs + "patternFill", new XAttribute("patternType", "none")))),
                new XElement(SpreadsheetNs + "borders",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "border")),
                new XElement(SpreadsheetNs + "cellStyleXfs",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf")),
                new XElement(SpreadsheetNs + "cellXfs",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf", new XAttribute("xfId", "0")))));
    }

    private static XDocument BuildWorksheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var rowElements = new List<XElement>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = new List<XElement>();
            var row = rows[rowIndex];
            for (var colIndex = 0; colIndex < row.Count; colIndex++)
            {
                var value = row[colIndex];
                if (value is null)
                {
                    continue;
                }

                var cellRef = $"{ToColumnName(colIndex)}{rowIndex + 1}";
                if (value is decimal or double or float or int or long)
                {
                    cells.Add(new XElement(SpreadsheetNs + "c",
                        new XAttribute("r", cellRef),
                        new XElement(SpreadsheetNs + "v", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0")));
                }
                else
                {
                    cells.Add(new XElement(SpreadsheetNs + "c",
                        new XAttribute("r", cellRef),
                        new XAttribute("t", "inlineStr"),
                        new XElement(SpreadsheetNs + "is",
                            new XElement(SpreadsheetNs + "t", value.ToString() ?? string.Empty))));
                }
            }

            rowElements.Add(new XElement(SpreadsheetNs + "row",
                new XAttribute("r", rowIndex + 1),
                cells));
        }

        return new XDocument(
            new XElement(SpreadsheetNs + "worksheet",
                new XElement(SpreadsheetNs + "sheetData", rowElements)));
    }

    private static string ToColumnName(int index)
    {
        var n = index + 1;
        var chars = new Stack<char>();
        while (n > 0)
        {
            var remainder = (n - 1) % 26;
            chars.Push((char)('A' + remainder));
            n = (n - 1) / 26;
        }

        return new string(chars.ToArray());
    }
}
