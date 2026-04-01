using System.IO;
using System.IO.Compression;
using System.Security;
using System.Xml.Linq;
using Accounting.Infrastructure.Logging;

namespace Accounting.Services;

public sealed class AccountImportExportXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public AccessOperationResult Export(string filePath, IReadOnlyCollection<ManagedAccount> accounts)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AccessOperationResult(false, "Path export master akun tidak valid.");
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var rows = new List<List<object?>>
            {
                new()
                {
                    "Code",
                    "Name",
                    "AccountType",
                    "ParentAccountCode",
                    "IsActive",
                    "RequiresDepartment",
                    "RequiresProject",
                    "RequiresCostCenter",
                    "RequiresSubledger",
                    "AllowedSubledgerType"
                }
            };

            foreach (var account in (accounts ?? Array.Empty<ManagedAccount>()).OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new List<object?>
                {
                    account.Code,
                    account.Name,
                    account.AccountType,
                    account.ParentAccountCode,
                    account.IsActive ? "true" : "false",
                    account.RequiresDepartment ? "true" : "false",
                    account.RequiresProject ? "true" : "false",
                    account.RequiresCostCenter ? "true" : "false",
                    account.RequiresSubledger ? "true" : "false",
                    account.AllowedSubledgerType
                });
            }

            using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", BuildSingleSheetContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteEntry(archive, "xl/workbook.xml", BuildSingleSheetWorkbookXml());
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildSingleSheetWorkbookRelsXml());
            WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));

            return new AccessOperationResult(true, "Export master akun berhasil dibuat.");
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(AccountImportExportXlsxService),
                "ExportMasterAccountsFailed",
                $"action=export_master_accounts file_path={filePath}",
                ex);
            return new AccessOperationResult(false, $"Gagal export master akun: {ex.Message}");
        }
    }

    public AccountImportParseResult Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new AccountImportParseResult
            {
                IsSuccess = false,
                Message = "File import master akun tidak ditemukan."
            };
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var workbook = LoadXDocument(archive, "xl/workbook.xml");
            var workbookRels = LoadXDocument(archive, "xl/_rels/workbook.xml.rels");
            var sharedStrings = TryLoadSharedStrings(archive);
            var sheetTargets = ResolveSheetTargets(workbook, workbookRels);

            if (!sheetTargets.TryGetValue("Accounts", out var accountsPath))
            {
                return new AccountImportParseResult
                {
                    IsSuccess = false,
                    Message = "Format import tidak valid. Gunakan sheet 'Accounts'."
                };
            }

            var rows = ReadRows(LoadXDocument(archive, accountsPath), sharedStrings);
            var errors = new List<InventoryImportError>();
            var accounts = ParseAccounts(rows, errors);

            if (accounts.Count == 0)
            {
                return new AccountImportParseResult
                {
                    IsSuccess = false,
                    Message = errors.Count > 0 ? "File import master akun tidak valid." : "Tidak ada data akun valid pada sheet Accounts.",
                    Errors = errors
                };
            }

            if (errors.Count > 0)
            {
                return new AccountImportParseResult
                {
                    IsSuccess = false,
                    Message = BuildValidationErrorMessage(errors),
                    Bundle = new AccountImportBundle { Accounts = accounts },
                    Errors = errors
                };
            }

            return new AccountImportParseResult
            {
                IsSuccess = true,
                Message = $"File valid. Total akun: {accounts.Count}.",
                Bundle = new AccountImportBundle { Accounts = accounts }
            };
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogError(
                nameof(AccountImportExportXlsxService),
                "ParseMasterAccountsFailed",
                $"action=parse_master_accounts file_path={filePath}",
                ex);

            return new AccountImportParseResult
            {
                IsSuccess = false,
                Message = $"Gagal membaca file XLSX master akun: {ex.Message}"
            };
        }
    }

    private static List<AccountImportRow> ParseAccounts(
        IReadOnlyList<Dictionary<int, string>> rows,
        ICollection<InventoryImportError> errors)
    {
        var output = new List<AccountImportRow>();
        if (rows.Count <= 1)
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Accounts",
                RowNumber = 0,
                Message = "Sheet Accounts kosong."
            });
            return output;
        }

        var columnMap = BuildColumnMap(rows[0]);
        if (!columnMap.TryGetValue("CODE", out var codeCol) ||
            !columnMap.TryGetValue("NAME", out var nameCol))
        {
            errors.Add(new InventoryImportError
            {
                SheetName = "Accounts",
                RowNumber = 1,
                Message = "Kolom wajib: Code, Name."
            });
            return output;
        }

        var accountTypeCol = GetColumnIndex(columnMap, "ACCOUNTTYPE");
        var parentCodeCol = GetColumnIndex(columnMap, "PARENTACCOUNTCODE");
        var isActiveCol = GetColumnIndex(columnMap, "ISACTIVE");
        var requiresDepartmentCol = GetColumnIndex(columnMap, "REQUIRESDEPARTMENT");
        var requiresProjectCol = GetColumnIndex(columnMap, "REQUIRESPROJECT");
        var requiresCostCenterCol = GetColumnIndex(columnMap, "REQUIRESCOSTCENTER");
        var requiresSubledgerCol = GetColumnIndex(columnMap, "REQUIRESSUBLEDGER");
        var allowedSubledgerTypeCol = GetColumnIndex(columnMap, "ALLOWEDSUBLEDGERTYPE");

        var uniqueCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNumber = i + 1;
            var code = GetCell(row, codeCol).ToUpperInvariant();
            var name = GetCell(row, nameCol);
            var accountType = GetCell(row, accountTypeCol).ToUpperInvariant();
            var parentAccountCode = GetCell(row, parentCodeCol).ToUpperInvariant();
            var isActiveText = GetCell(row, isActiveCol);
            var requiresDepartmentText = GetCell(row, requiresDepartmentCol);
            var requiresProjectText = GetCell(row, requiresProjectCol);
            var requiresCostCenterText = GetCell(row, requiresCostCenterCol);
            var requiresSubledgerText = GetCell(row, requiresSubledgerCol);
            var allowedSubledgerType = GetCell(row, allowedSubledgerTypeCol).ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(code) &&
                string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(accountType) &&
                string.IsNullOrWhiteSpace(parentAccountCode) &&
                string.IsNullOrWhiteSpace(isActiveText) &&
                string.IsNullOrWhiteSpace(requiresDepartmentText) &&
                string.IsNullOrWhiteSpace(requiresProjectText) &&
                string.IsNullOrWhiteSpace(requiresCostCenterText) &&
                string.IsNullOrWhiteSpace(requiresSubledgerText) &&
                string.IsNullOrWhiteSpace(allowedSubledgerType))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = "Code dan Name wajib diisi."
                });
                continue;
            }

            if (!CoaAccountCodeRules.TryDeriveAccountType(code, out var derivedAccountType))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Prefix kode akun tidak dikenali untuk struktur COA aktif: {code}."
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(accountType) &&
                !string.Equals(accountType, derivedAccountType, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"AccountType {accountType} tidak cocok dengan prefix kode akun {code}. Tipe seharusnya {derivedAccountType}."
                });
                continue;
            }

            if (!uniqueCodes.Add(code))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Code duplikat dalam file: {code}."
                });
                continue;
            }

            if (!TryParseBooleanCell(isActiveText, defaultValue: true, out var isActive))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai IsActive tidak valid: {isActiveText}."
                });
                continue;
            }

            if (!TryParseBooleanCell(requiresDepartmentText, defaultValue: false, out var requiresDepartment))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai RequiresDepartment tidak valid: {requiresDepartmentText}."
                });
                continue;
            }

            if (!TryParseBooleanCell(requiresProjectText, defaultValue: false, out var requiresProject))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai RequiresProject tidak valid: {requiresProjectText}."
                });
                continue;
            }

            if (!TryParseBooleanCell(requiresCostCenterText, defaultValue: false, out var requiresCostCenter))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai RequiresCostCenter tidak valid: {requiresCostCenterText}."
                });
                continue;
            }

            if (!TryParseBooleanCell(requiresSubledgerText, defaultValue: false, out var requiresSubledger))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai RequiresSubledger tidak valid: {requiresSubledgerText}."
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(allowedSubledgerType) &&
                allowedSubledgerType is not ("VENDOR" or "CUSTOMER" or "EMPLOYEE"))
            {
                errors.Add(new InventoryImportError
                {
                    SheetName = "Accounts",
                    RowNumber = rowNumber,
                    Message = $"Nilai AllowedSubledgerType tidak valid: {allowedSubledgerType}."
                });
                continue;
            }

            output.Add(new AccountImportRow
            {
                RowNumber = rowNumber,
                Code = code,
                Name = name,
                AccountType = derivedAccountType,
                ParentAccountCode = parentAccountCode,
                IsActive = isActive,
                RequiresDepartment = requiresDepartment,
                RequiresProject = requiresProject,
                RequiresCostCenter = requiresCostCenter,
                RequiresSubledger = requiresSubledger || !string.IsNullOrWhiteSpace(allowedSubledgerType),
                AllowedSubledgerType = allowedSubledgerType
            });
        }

        return output;
    }

    private static bool TryParseBooleanCell(string value, bool defaultValue, out bool result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = defaultValue;
            return true;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "TRUE":
            case "1":
            case "YES":
            case "Y":
            case "AKTIF":
                result = true;
                return true;
            case "FALSE":
            case "0":
            case "NO":
            case "N":
            case "NONAKTIF":
            case "NON-AKTIF":
                result = false;
                return true;
            default:
                result = defaultValue;
                return false;
        }
    }

    private static string BuildValidationErrorMessage(IReadOnlyCollection<InventoryImportError> errors)
    {
        if (errors.Count == 0)
        {
            return "Validasi file import master akun gagal.";
        }

        if (errors.Count == 1)
        {
            return "Validasi file import master akun gagal. Terdapat 1 error.";
        }

        return $"Validasi file import master akun gagal. Terdapat {errors.Count} error.";
    }

    private static Dictionary<string, int> BuildColumnMap(Dictionary<int, string> headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headerRow)
        {
            var normalized = NormalizeColumnHeader(pair.Value);
            if (string.IsNullOrWhiteSpace(normalized) || map.ContainsKey(normalized))
            {
                continue;
            }

            map[normalized] = pair.Key;
        }

        return map;
    }

    private static int GetColumnIndex(IReadOnlyDictionary<string, int> columnMap, string key)
    {
        return columnMap.TryGetValue(key, out var value) ? value : -1;
    }

    private static string NormalizeColumnHeader(string value)
    {
        return string.Concat((value ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-')).Trim().ToUpperInvariant();
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int columnIndex)
    {
        if (columnIndex < 0 || !row.TryGetValue(columnIndex, out var value))
        {
            return string.Empty;
        }

        return (value ?? string.Empty).Trim();
    }

    private static XDocument LoadXDocument(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath) ?? throw new InvalidOperationException($"Entry {entryPath} tidak ditemukan.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static IReadOnlyList<string>? TryLoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Root?
            .Elements(SpreadsheetNs + "si")
            .Select(ExtractSharedString)
            .ToList();
    }

    private static string ExtractSharedString(XElement element)
    {
        var direct = string.Concat(element.Elements(SpreadsheetNs + "t").Select(x => x.Value));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        return string.Concat(
            element.Elements(SpreadsheetNs + "r")
                   .SelectMany(x => x.Elements(SpreadsheetNs + "t"))
                   .Select(x => x.Value));
    }

    private static Dictionary<string, string> ResolveSheetTargets(XDocument workbook, XDocument workbookRels)
    {
        var relationshipMap = workbookRels.Root?
            .Elements(PackageRelNs + "Relationship")
            .Where(x => x.Attribute("Id") is not null && x.Attribute("Target") is not null)
            .ToDictionary(
                x => x.Attribute("Id")!.Value,
                x => NormalizeWorkbookTarget(x.Attribute("Target")!.Value),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sheets = workbook.Root?
            .Element(SpreadsheetNs + "sheets")?
            .Elements(SpreadsheetNs + "sheet")
            ?? Enumerable.Empty<XElement>();

        foreach (var sheet in sheets)
        {
            var name = sheet.Attribute("name")?.Value;
            var relId = sheet.Attribute(RelNs + "id")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relId))
            {
                continue;
            }

            if (relationshipMap.TryGetValue(relId, out var target))
            {
                result[name] = target;
            }
        }

        return result;
    }

    private static string NormalizeWorkbookTarget(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/');
        if (normalized.StartsWith("/"))
        {
            normalized = normalized[1..];
        }

        if (normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith("worksheets/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("theme/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("styles", StringComparison.OrdinalIgnoreCase))
        {
            return $"xl/{normalized}";
        }

        return $"xl/{normalized}";
    }

    private static List<Dictionary<int, string>> ReadRows(XDocument worksheet, IReadOnlyList<string>? sharedStrings)
    {
        var rows = new List<Dictionary<int, string>>();
        var sheetData = worksheet.Root?.Element(SpreadsheetNs + "sheetData");
        if (sheetData is null)
        {
            return rows;
        }

        foreach (var row in sheetData.Elements(SpreadsheetNs + "row"))
        {
            var values = new Dictionary<int, string>();
            foreach (var cell in row.Elements(SpreadsheetNs + "c"))
            {
                var reference = cell.Attribute("r")?.Value;
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                var columnIndex = GetColumnIndexFromReference(reference);
                values[columnIndex] = ReadCellValue(cell, sharedStrings);
            }

            rows.Add(values);
        }

        return rows;
    }

    private static int GetColumnIndexFromReference(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        var result = 0;
        foreach (var letter in letters)
        {
            result = (result * 26) + (char.ToUpperInvariant(letter) - 'A' + 1);
        }

        return Math.Max(0, result - 1);
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string>? sharedStrings)
    {
        var cellType = cell.Attribute("t")?.Value;
        var valueElement = cell.Element(SpreadsheetNs + "v");
        if (valueElement is null)
        {
            var inlineString = cell.Element(SpreadsheetNs + "is");
            return inlineString is null
                ? string.Empty
                : ExtractSharedString(inlineString);
        }

        var rawValue = valueElement.Value;
        if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase) &&
            sharedStrings is not null &&
            int.TryParse(rawValue, out var index) &&
            index >= 0 &&
            index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        return rawValue;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string BuildSingleSheetContentTypesXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""";
    }

    private static string BuildRootRelsXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""";
    }

    private static string BuildSingleSheetWorkbookXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets>
    <sheet name="Accounts" sheetId="1" r:id="rId1"/>
  </sheets>
</workbook>
""";
    }

    private static string BuildSingleSheetWorkbookRelsXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";
    }

    private static string BuildStylesXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="1">
    <font>
      <sz val="11"/>
      <color theme="1"/>
      <name val="Calibri"/>
      <family val="2"/>
    </font>
  </fonts>
  <fills count="2">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
  </fills>
  <borders count="1">
    <border><left/><right/><top/><bottom/><diagonal/></border>
  </borders>
  <cellStyleXfs count="1">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
  </cellStyleXfs>
  <cellXfs count="1">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
  </cellXfs>
  <cellStyles count="1">
    <cellStyle name="Normal" xfId="0" builtinId="0"/>
  </cellStyles>
</styleSheet>
""";
    }

    private static string BuildWorksheetXml(IReadOnlyList<List<object?>> rows)
    {
        var rowXml = new List<string>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cellXml = new List<string>();
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var value = row[columnIndex];
                if (value is null)
                {
                    continue;
                }

                var text = EscapeXml(value.ToString() ?? string.Empty);
                var cellReference = $"{GetColumnName(columnIndex)}{rowIndex + 1}";
                cellXml.Add($"""<c r="{cellReference}" t="inlineStr"><is><t xml:space="preserve">{text}</t></is></c>""");
            }

            rowXml.Add($"""<row r="{rowIndex + 1}">{string.Concat(cellXml)}</row>""");
        }

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <sheetData>
    {{string.Concat(rowXml)}}
  </sheetData>
</worksheet>
""";
    }

    private static string GetColumnName(int index)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var value = index + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = alphabet[value % 26] + result;
            value /= 26;
        }

        return result;
    }

    private static string EscapeXml(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }
}
