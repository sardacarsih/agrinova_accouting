using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class InventoryImportXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public AccessOperationResult CreateTemplate(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AccessOperationResult(false, "Path template import tidak valid.");
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var categoryRows = new List<List<object?>>
            {
                new() { "CategoryCode", "CategoryName", "AccountCode", "IsActive" },
                new() { "RAW", "Bahan Baku", "HO.11000.001", "true" }
            };
            var itemRows = new List<List<object?>>
            {
                new() { "ItemCode", "ItemName", "Uom", "CategoryCode", "IsActive" },
                new() { "RAW-001", "Bahan A", "PCS", "RAW", "true" }
            };

            using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", BuildTwoSheetContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteEntry(archive, "xl/workbook.xml", BuildTwoSheetWorkbookXml());
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildTwoSheetWorkbookRelsXml());
            WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(categoryRows));
            WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildWorksheetXml(itemRows));

            return new AccessOperationResult(true, "Template import inventory berhasil dibuat.");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryImportXlsxService),
                "CreateInventoryImportTemplateFailed",
                $"action=create_inventory_import_template file_path={filePath}",
                ex);
            return new AccessOperationResult(false, $"Gagal membuat template import: {ex.Message}");
        }
    }

    public InventoryImportParseResult Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new InventoryImportParseResult
            {
                IsSuccess = false,
                Message = "File import inventory tidak ditemukan."
            };
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var workbook = LoadXDocument(archive, "xl/workbook.xml");
            var workbookRels = LoadXDocument(archive, "xl/_rels/workbook.xml.rels");
            var sharedStrings = TryLoadSharedStrings(archive);
            var sheetTargets = ResolveSheetTargets(workbook, workbookRels);

            if (!sheetTargets.TryGetValue("Categories", out var categoriesPath) ||
                !sheetTargets.TryGetValue("Items", out var itemsPath))
            {
                return new InventoryImportParseResult
                {
                    IsSuccess = false,
                    Message = "Format import tidak valid. Gunakan sheet 'Categories' dan 'Items'."
                };
            }

            var categoryRows = ReadRows(LoadXDocument(archive, categoriesPath), sharedStrings);
            var itemRows = ReadRows(LoadXDocument(archive, itemsPath), sharedStrings);

            var errors = new List<InventoryImportError>();
            var categories = ParseCategories(categoryRows, errors);
            var items = ParseItems(itemRows, errors);

            if (categories.Count == 0 && items.Count == 0)
            {
                return new InventoryImportParseResult
                {
                    IsSuccess = false,
                    Message = "Tidak ada data valid pada sheet Categories/Items.",
                    Errors = errors
                };
            }

            if (errors.Count > 0)
            {
                return new InventoryImportParseResult
                {
                    IsSuccess = false,
                    Message = BuildValidationErrorMessage(errors),
                    Bundle = new InventoryImportBundle
                    {
                        Categories = categories,
                        Items = items
                    },
                    Errors = errors
                };
            }

            return new InventoryImportParseResult
            {
                IsSuccess = true,
                Message = $"File valid. Kategori: {categories.Count}, Item: {items.Count}.",
                Bundle = new InventoryImportBundle
                {
                    Categories = categories,
                    Items = items
                }
            };
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(InventoryImportXlsxService),
                "ParseInventoryImportXlsxFailed",
                $"action=parse_inventory_import_xlsx file_path={filePath}",
                ex);

            return new InventoryImportParseResult
            {
                IsSuccess = false,
                Message = $"Gagal membaca file XLSX inventory: {ex.Message}"
            };
        }
    }

    private static List<InventoryImportCategoryRow> ParseCategories(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<InventoryImportCategoryRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Categories",
                RowNumber = 0,
                Message = "Sheet Categories kosong."
            });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("CATEGORYCODE", out var codeCol) ||
            !columnMap.TryGetValue("CATEGORYNAME", out var nameCol))
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Categories",
                RowNumber = 1,
                Message = "Kolom wajib: CategoryCode, CategoryName."
            });
            return output;
        }

        var accountCodeCol = GetColumnIndex(columnMap, "ACCOUNTCODE");
        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var uniqueCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var accountCode = GetCell(row, accountCodeCol).ToUpperInvariant();
            var isActiveText = GetCell(row, isActiveCol);

            if (string.IsNullOrWhiteSpace(code) &&
                string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(accountCode) &&
                string.IsNullOrWhiteSpace(isActiveText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Categories",
                    RowNumber = rowNumber,
                    Message = "CategoryCode dan CategoryName wajib diisi."
                });
                continue;
            }

            if (!uniqueCodes.Add(code))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Categories",
                    RowNumber = rowNumber,
                    Message = $"CategoryCode duplikat dalam file: {code}."
                });
                continue;
            }

            if (!TryParseIsActive(isActiveText, out var isActive))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Categories",
                    RowNumber = rowNumber,
                    Message = "IsActive tidak valid (gunakan true/false, 1/0, yes/no)."
                });
                continue;
            }

            output.Add(new InventoryImportCategoryRow
            {
                RowNumber = rowNumber,
                Code = code,
                Name = name,
                AccountCode = accountCode,
                IsActive = isActive
            });
        }

        return output;
    }

    private static List<InventoryImportItemRow> ParseItems(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<InventoryImportItemRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Items",
                RowNumber = 0,
                Message = "Sheet Items kosong."
            });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("ITEMCODE", out var codeCol) ||
            !columnMap.TryGetValue("ITEMNAME", out var nameCol) ||
            !columnMap.TryGetValue("CATEGORYCODE", out var categoryCodeCol))
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Items",
                RowNumber = 1,
                Message = "Kolom wajib: ItemCode, ItemName, CategoryCode."
            });
            return output;
        }

        var uomCol = GetColumnIndex(columnMap, "UOM");
        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var uniqueCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var uom = GetCell(row, uomCol).ToUpperInvariant();
            var categoryCode = GetCell(row, categoryCodeCol).ToUpperInvariant();
            var isActiveText = GetCell(row, isActiveCol);

            if (string.IsNullOrWhiteSpace(code) &&
                string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(uom) &&
                string.IsNullOrWhiteSpace(categoryCode) &&
                string.IsNullOrWhiteSpace(isActiveText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(code) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(categoryCode))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Items",
                    RowNumber = rowNumber,
                    Message = "ItemCode, ItemName, dan CategoryCode wajib diisi."
                });
                continue;
            }

            if (!uniqueCodes.Add(code))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Items",
                    RowNumber = rowNumber,
                    Message = $"ItemCode duplikat dalam file: {code}."
                });
                continue;
            }

            if (!TryParseIsActive(isActiveText, out var isActive))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Items",
                    RowNumber = rowNumber,
                    Message = "IsActive tidak valid (gunakan true/false, 1/0, yes/no)."
                });
                continue;
            }

            output.Add(new InventoryImportItemRow
            {
                RowNumber = rowNumber,
                Code = code,
                Name = name,
                Uom = string.IsNullOrWhiteSpace(uom) ? "PCS" : uom,
                CategoryCode = categoryCode,
                IsActive = isActive
            });
        }

        return output;
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyDictionary<int, string> headerRow)
    {
        var output = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headerRow)
        {
            var key = NormalizeColumnName(pair.Value);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            output[key] = pair.Key;
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

    private static string GetCell(IReadOnlyDictionary<int, string> row, int columnIndex)
    {
        if (columnIndex < 0)
        {
            return string.Empty;
        }

        return row.TryGetValue(columnIndex, out var value) ? value.Trim() : string.Empty;
    }

    private static bool TryParseIsActive(string value, out bool isActive)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            isActive = true;
            return true;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized is "TRUE" or "T" or "YES" or "Y" or "1")
        {
            isActive = true;
            return true;
        }

        if (normalized is "FALSE" or "F" or "NO" or "N" or "0")
        {
            isActive = false;
            return true;
        }

        isActive = true;
        return false;
    }

    private static string BuildValidationErrorMessage(IReadOnlyCollection<InventoryImportError> errors)
    {
        var preview = errors
            .Take(5)
            .Select(x => $"{x.SheetName} r{(x.RowNumber <= 0 ? 1 : x.RowNumber)}: {x.Message}")
            .ToList();

        return $"Validasi import gagal ({errors.Count} error). {string.Join(" | ", preview)}";
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

    private static XDocument BuildTwoSheetWorkbookXml()
    {
        return new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "Categories"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")),
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "Items"),
                        new XAttribute("sheetId", "2"),
                        new XAttribute(RelNs + "id", "rId2")))));
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
                    new XElement(SpreadsheetNs + "border",
                        new XElement(SpreadsheetNs + "left"),
                        new XElement(SpreadsheetNs + "right"),
                        new XElement(SpreadsheetNs + "top"),
                        new XElement(SpreadsheetNs + "bottom"),
                        new XElement(SpreadsheetNs + "diagonal"))),
                new XElement(SpreadsheetNs + "cellStyleXfs",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "0"))),
                new XElement(SpreadsheetNs + "cellXfs",
                    new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("fillId", "0"),
                        new XAttribute("borderId", "0"),
                        new XAttribute("xfId", "0")))));
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
                cells.Add(new XElement(SpreadsheetNs + "c",
                    new XAttribute("r", cellRef),
                    new XAttribute("t", "inlineStr"),
                    new XElement(SpreadsheetNs + "is",
                        new XElement(SpreadsheetNs + "t", value.ToString() ?? string.Empty))));
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
