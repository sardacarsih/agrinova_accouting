using System.IO;
using System.IO.Compression;
using System.Security;
using System.Xml.Linq;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class EstateHierarchyImportExportXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public AccessOperationResult Export(string filePath, EstateHierarchyWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AccessOperationResult(false, "Path export estate hierarchy tidak valid.");
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var estateRows = new List<List<object?>>
            {
                new() { "Code", "Name", "IsActive" }
            };
            var divisionRows = new List<List<object?>>
            {
                new() { "EstateCode", "Code", "Name", "IsActive" }
            };
            var blockRows = new List<List<object?>>
            {
                new() { "EstateCode", "DivisionCode", "Code", "Name", "IsActive" }
            };

            foreach (var estate in (workspace?.Estates ?? []).OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
            {
                estateRows.Add(new List<object?> { estate.Code, estate.Name, estate.IsActive ? "true" : "false" });

                foreach (var division in estate.Divisions.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
                {
                    divisionRows.Add(new List<object?>
                    {
                        estate.Code,
                        division.Code,
                        division.Name,
                        division.IsActive ? "true" : "false"
                    });

                    foreach (var block in division.Blocks.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
                    {
                        blockRows.Add(new List<object?>
                        {
                            estate.Code,
                            division.Code,
                            block.Code,
                            block.Name,
                            block.IsActive ? "true" : "false"
                        });
                    }
                }
            }

            using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
            WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(estateRows));
            WriteEntry(archive, "xl/worksheets/sheet2.xml", BuildWorksheetXml(divisionRows));
            WriteEntry(archive, "xl/worksheets/sheet3.xml", BuildWorksheetXml(blockRows));

            return new AccessOperationResult(true, "Export estate/division/blok berhasil dibuat.");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(EstateHierarchyImportExportXlsxService),
                "ExportEstateHierarchyFailed",
                $"action=export_estate_hierarchy file_path={filePath}",
                ex);
            return new AccessOperationResult(false, $"Gagal export estate/division/blok: {ex.Message}");
        }
    }

    public EstateHierarchyImportParseResult Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new EstateHierarchyImportParseResult
            {
                IsSuccess = false,
                Message = "File import estate/division/blok tidak ditemukan."
            };
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var workbook = LoadXDocument(archive, "xl/workbook.xml");
            var workbookRels = LoadXDocument(archive, "xl/_rels/workbook.xml.rels");
            var sharedStrings = TryLoadSharedStrings(archive);
            var sheetTargets = ResolveSheetTargets(workbook, workbookRels);

            if (!sheetTargets.TryGetValue("Estates", out var estatesPath) ||
                !sheetTargets.TryGetValue("Divisions", out var divisionsPath) ||
                !sheetTargets.TryGetValue("Blocks", out var blocksPath))
            {
                return new EstateHierarchyImportParseResult
                {
                    IsSuccess = false,
                    Message = "Format import tidak valid. Gunakan sheet 'Estates', 'Divisions', dan 'Blocks'."
                };
            }

            var estateRows = ReadRows(LoadXDocument(archive, estatesPath), sharedStrings);
            var divisionRows = ReadRows(LoadXDocument(archive, divisionsPath), sharedStrings);
            var blockRows = ReadRows(LoadXDocument(archive, blocksPath), sharedStrings);

            var errors = new List<InventoryImportError>();
            var estates = ParseEstates(estateRows, errors);
            var divisions = ParseDivisions(divisionRows, errors);
            var blocks = ParseBlocks(blockRows, errors);

            if (estates.Count == 0 && divisions.Count == 0 && blocks.Count == 0)
            {
                return new EstateHierarchyImportParseResult
                {
                    IsSuccess = false,
                    Message = errors.Count > 0
                        ? "File import estate/division/blok tidak valid."
                        : "Tidak ada data valid pada workbook import estate/division/blok.",
                    Errors = errors
                };
            }

            if (errors.Count > 0)
            {
                return new EstateHierarchyImportParseResult
                {
                    IsSuccess = false,
                    Message = BuildValidationErrorMessage(errors),
                    Bundle = new EstateHierarchyImportBundle
                    {
                        Estates = estates,
                        Divisions = divisions,
                        Blocks = blocks
                    },
                    Errors = errors
                };
            }

            return new EstateHierarchyImportParseResult
            {
                IsSuccess = true,
                Message = $"File valid. Estate: {estates.Count}, Divisi: {divisions.Count}, Blok: {blocks.Count}.",
                Bundle = new EstateHierarchyImportBundle
                {
                    Estates = estates,
                    Divisions = divisions,
                    Blocks = blocks
                }
            };
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(EstateHierarchyImportExportXlsxService),
                "ParseEstateHierarchyFailed",
                $"action=parse_estate_hierarchy file_path={filePath}",
                ex);

            return new EstateHierarchyImportParseResult
            {
                IsSuccess = false,
                Message = $"Gagal membaca file XLSX estate/division/blok: {ex.Message}"
            };
        }
    }

    private static List<EstateImportRow> ParseEstates(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<EstateImportRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = 0, Message = "Sheet Estates kosong." });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("CODE", out var codeCol) || !columnMap.TryGetValue("NAME", out var nameCol))
        {
            errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = 1, Message = "Kolom wajib: Code, Name." });
            return output;
        }

        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var uniqueCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var isActiveText = GetCell(row, isActiveCol);

            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(isActiveText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = rowNumber, Message = "Code dan Name wajib diisi." });
                continue;
            }

            if (!uniqueCodes.Add(code))
            {
                errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = rowNumber, Message = $"Code duplikat dalam file: {code}." });
                continue;
            }

            if (!TryParseBooleanCell(isActiveText, true, out var isActive))
            {
                errors.Add(new InventoryImportError { SheetName = "Estates", RowNumber = rowNumber, Message = $"Nilai IsActive tidak valid: {isActiveText}." });
                continue;
            }

            output.Add(new EstateImportRow
            {
                RowNumber = rowNumber,
                Code = code,
                Name = name,
                IsActive = isActive
            });
        }

        return output;
    }

    private static List<DivisionImportRow> ParseDivisions(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<DivisionImportRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = 0, Message = "Sheet Divisions kosong." });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("ESTATECODE", out var estateCodeCol) ||
            !columnMap.TryGetValue("CODE", out var codeCol) ||
            !columnMap.TryGetValue("NAME", out var nameCol))
        {
            errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = 1, Message = "Kolom wajib: EstateCode, Code, Name." });
            return output;
        }

        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var estateCode = GetCell(row, estateCodeCol).ToUpperInvariant();
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var isActiveText = GetCell(row, isActiveCol);

            if (string.IsNullOrWhiteSpace(estateCode) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(isActiveText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(estateCode) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = rowNumber, Message = "EstateCode, Code, dan Name wajib diisi." });
                continue;
            }

            var uniqueKey = $"{estateCode}|{code}";
            if (!uniqueKeys.Add(uniqueKey))
            {
                errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = rowNumber, Message = $"Kombinasi EstateCode/Code duplikat: {estateCode}/{code}." });
                continue;
            }

            if (!TryParseBooleanCell(isActiveText, true, out var isActive))
            {
                errors.Add(new InventoryImportError { SheetName = "Divisions", RowNumber = rowNumber, Message = $"Nilai IsActive tidak valid: {isActiveText}." });
                continue;
            }

            output.Add(new DivisionImportRow
            {
                RowNumber = rowNumber,
                EstateCode = estateCode,
                Code = code,
                Name = name,
                IsActive = isActive
            });
        }

        return output;
    }

    private static List<BlockImportRow> ParseBlocks(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<BlockImportRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = 0, Message = "Sheet Blocks kosong." });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("ESTATECODE", out var estateCodeCol) ||
            !columnMap.TryGetValue("DIVISIONCODE", out var divisionCodeCol) ||
            !columnMap.TryGetValue("CODE", out var codeCol) ||
            !columnMap.TryGetValue("NAME", out var nameCol))
        {
            errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = 1, Message = "Kolom wajib: EstateCode, DivisionCode, Code, Name." });
            return output;
        }

        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var estateCode = GetCell(row, estateCodeCol).ToUpperInvariant();
            var divisionCode = GetCell(row, divisionCodeCol).ToUpperInvariant();
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var isActiveText = GetCell(row, isActiveCol);

            if (string.IsNullOrWhiteSpace(estateCode) &&
                string.IsNullOrWhiteSpace(divisionCode) &&
                string.IsNullOrWhiteSpace(code) &&
                string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(isActiveText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(estateCode) || string.IsNullOrWhiteSpace(divisionCode) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = rowNumber, Message = "EstateCode, DivisionCode, Code, dan Name wajib diisi." });
                continue;
            }

            var uniqueKey = $"{estateCode}|{divisionCode}|{code}";
            if (!uniqueKeys.Add(uniqueKey))
            {
                errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = rowNumber, Message = $"Kombinasi EstateCode/DivisionCode/Code duplikat: {estateCode}/{divisionCode}/{code}." });
                continue;
            }

            if (!TryParseBooleanCell(isActiveText, true, out var isActive))
            {
                errors.Add(new InventoryImportError { SheetName = "Blocks", RowNumber = rowNumber, Message = $"Nilai IsActive tidak valid: {isActiveText}." });
                continue;
            }

            output.Add(new BlockImportRow
            {
                RowNumber = rowNumber,
                EstateCode = estateCode,
                DivisionCode = divisionCode,
                Code = code,
                Name = name,
                IsActive = isActive
            });
        }

        return output;
    }

    private static string BuildValidationErrorMessage(IReadOnlyCollection<InventoryImportError> errors)
    {
        return errors.Count switch
        {
            <= 0 => "File import tidak valid.",
            1 => "Ditemukan 1 error validasi pada workbook import.",
            _ => $"Ditemukan {errors.Count} error validasi pada workbook import."
        };
    }

    private static Dictionary<string, int> BuildColumnMap(IReadOnlyDictionary<int, string> headerRow)
    {
        return headerRow
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .GroupBy(x => NormalizeColumnName(x.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Key, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeColumnName(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static int GetColumnIndex(IReadOnlyDictionary<string, int> columnMap, string columnName)
    {
        return columnMap.TryGetValue(columnName, out var index) ? index : -1;
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int columnIndex)
    {
        return columnIndex >= 0 && row.TryGetValue(columnIndex, out var value)
            ? value.Trim()
            : string.Empty;
    }

    private static bool TryParseBooleanCell(string? value, bool defaultValue, out bool result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = defaultValue;
            return true;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "TRUE":
            case "YES":
            case "Y":
            case "1":
                result = true;
                return true;
            case "FALSE":
            case "NO":
            case "N":
            case "0":
                result = false;
                return true;
            default:
                result = defaultValue;
                return false;
        }
    }

    private static XDocument LoadXDocument(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Entry XLSX tidak ditemukan: {path}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static List<string>? TryLoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(SpreadsheetNs + "si")
            .Select(si => string.Concat(si.Descendants(SpreadsheetNs + "t").Select(t => t.Value)))
            .ToList();
    }

    private static Dictionary<string, string> ResolveSheetTargets(XDocument workbook, XDocument workbookRels)
    {
        var ridToTarget = workbookRels
            .Descendants(PackageRelNs + "Relationship")
            .Where(x => x.Attribute("Id") is not null && x.Attribute("Target") is not null)
            .ToDictionary(
                x => x.Attribute("Id")!.Value,
                x => NormalizeWorkbookTarget(x.Attribute("Target")!.Value),
                StringComparer.OrdinalIgnoreCase);

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants(SpreadsheetNs + "sheet"))
        {
            var name = sheet.Attribute("name")?.Value ?? string.Empty;
            var relationshipId = sheet.Attribute(RelNs + "id")?.Value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(relationshipId) &&
                ridToTarget.TryGetValue(relationshipId, out var target))
            {
                output[name] = target;
            }
        }

        return output;
    }

    private static string NormalizeWorkbookTarget(string value)
    {
        if (value.StartsWith("/"))
        {
            return value.TrimStart('/');
        }

        return value.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"xl/{value.TrimStart('/')}";
    }

    private static List<Dictionary<int, string>> ReadRows(XDocument worksheet, IReadOnlyList<string>? sharedStrings)
    {
        var rows = new List<Dictionary<int, string>>();
        foreach (var row in worksheet.Descendants(SpreadsheetNs + "sheetData").Elements(SpreadsheetNs + "row"))
        {
            var values = new Dictionary<int, string>();
            foreach (var cell in row.Elements(SpreadsheetNs + "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? string.Empty;
                var columnIndex = GetColumnIndex(reference);
                if (columnIndex < 0)
                {
                    continue;
                }

                values[columnIndex] = ReadCellValue(cell, sharedStrings);
            }

            rows.Add(values);
        }

        return rows;
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string>? sharedStrings)
    {
        var type = cell.Attribute("t")?.Value ?? string.Empty;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(x => x.Value)).Trim();
        }

        var value = cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, out var sharedIndex) &&
            sharedStrings is not null &&
            sharedIndex >= 0 &&
            sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex].Trim();
        }

        return value.Trim();
    }

    private static int GetColumnIndex(string cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var column = 0;
        foreach (var character in cellReference.ToUpperInvariant())
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            column = (column * 26) + (character - 'A' + 1);
        }

        return column - 1;
    }

    private static XDocument BuildContentTypesXml()
    {
        return new XDocument(
            new XElement(
                XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
                new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("PartName", "/xl/worksheets/sheet2.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                    new XAttribute("PartName", "/xl/worksheets/sheet3.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
    }

    private static XDocument BuildRootRelsXml()
    {
        return new XDocument(
            new XElement(
                PackageRelNs + "Relationships",
                new XElement(
                    PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static XDocument BuildWorkbookXml()
    {
        return new XDocument(
            new XElement(
                SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(
                    SpreadsheetNs + "sheets",
                    new XElement(
                        SpreadsheetNs + "sheet",
                        new XAttribute("name", "Estates"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")),
                    new XElement(
                        SpreadsheetNs + "sheet",
                        new XAttribute("name", "Divisions"),
                        new XAttribute("sheetId", "2"),
                        new XAttribute(RelNs + "id", "rId2")),
                    new XElement(
                        SpreadsheetNs + "sheet",
                        new XAttribute("name", "Blocks"),
                        new XAttribute("sheetId", "3"),
                        new XAttribute(RelNs + "id", "rId3")))));
    }

    private static XDocument BuildWorkbookRelsXml()
    {
        return new XDocument(
            new XElement(
                PackageRelNs + "Relationships",
                new XElement(
                    PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(
                    PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet2.xml")),
                new XElement(
                    PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId3"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet3.xml")),
                new XElement(
                    PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId4"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))));
    }

    private static XDocument BuildStylesXml()
    {
        return new XDocument(
            new XElement(
                SpreadsheetNs + "styleSheet",
                new XElement(SpreadsheetNs + "fonts", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "font",
                        new XElement(SpreadsheetNs + "sz", new XAttribute("val", "11")),
                        new XElement(SpreadsheetNs + "name", new XAttribute("val", "Calibri")))),
                new XElement(SpreadsheetNs + "fills", new XAttribute("count", "2"),
                    new XElement(SpreadsheetNs + "fill", new XElement(SpreadsheetNs + "patternFill", new XAttribute("patternType", "none"))),
                    new XElement(SpreadsheetNs + "fill", new XElement(SpreadsheetNs + "patternFill", new XAttribute("patternType", "gray125")))),
                new XElement(SpreadsheetNs + "borders", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "border",
                        new XElement(SpreadsheetNs + "left"),
                        new XElement(SpreadsheetNs + "right"),
                        new XElement(SpreadsheetNs + "top"),
                        new XElement(SpreadsheetNs + "bottom"),
                        new XElement(SpreadsheetNs + "diagonal"))),
                new XElement(SpreadsheetNs + "cellStyleXfs", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0"))),
                new XElement(SpreadsheetNs + "cellXfs", new XAttribute("count", "1"),
                    new XElement(SpreadsheetNs + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0"), new XAttribute("xfId", "0")))));
    }

    private static XDocument BuildWorksheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sheetData = new XElement(SpreadsheetNs + "sheetData");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowElement = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowIndex + 1));
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var value = Convert.ToString(row[columnIndex]) ?? string.Empty;
                rowElement.Add(
                    new XElement(
                        SpreadsheetNs + "c",
                        new XAttribute("r", $"{GetColumnName(columnIndex)}{rowIndex + 1}"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(SpreadsheetNs + "is", new XElement(SpreadsheetNs + "t", SecurityElement.Escape(value) ?? string.Empty))));
            }

            sheetData.Add(rowElement);
        }

        return new XDocument(
            new XElement(
                SpreadsheetNs + "worksheet",
                new XElement(SpreadsheetNs + "sheetViews", new XElement(SpreadsheetNs + "sheetView", new XAttribute("workbookViewId", "0"))),
                new XElement(SpreadsheetNs + "sheetFormatPr", new XAttribute("defaultRowHeight", "15")),
                sheetData));
    }

    private static string GetColumnName(int columnIndex)
    {
        var dividend = columnIndex + 1;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = (char)('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }

    private static void WriteEntry(ZipArchive archive, string path, XDocument content)
    {
        WriteEntry(archive, path, content.ToString(SaveOptions.DisableFormatting));
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
