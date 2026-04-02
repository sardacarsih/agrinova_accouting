using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace Accounting.Services;

public sealed class InventoryReportXlsxService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    public void Export(string filePath, string sheetName, IReadOnlyCollection<IReadOnlyCollection<object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("Path export tidak valid.");
        }

        if (rows is null || rows.Count == 0)
        {
            throw new InvalidOperationException("Tidak ada data laporan untuk di-export.");
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
        WriteEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheetName));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
        WriteEntry(archive, "xl/styles.xml", BuildStylesXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
    }

    private static string BuildContentTypesXml()
    {
        var document = new XDocument(
            new XElement(ContentTypesNs + "Types",
                new XElement(ContentTypesNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypesNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new XElement(ContentTypesNs + "Override",
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))));
        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildRootRelsXml()
    {
        var document = new XDocument(
            new XElement(PackageRelNs + "Relationships",
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorkbookXml(string sheetName)
    {
        var safeSheetName = string.IsNullOrWhiteSpace(sheetName) ? "InventoryReport" : sheetName.Trim();
        if (safeSheetName.Length > 31)
        {
            safeSheetName = safeSheetName[..31];
        }

        var document = new XDocument(
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", RelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", safeSheetName),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(RelNs + "id", "rId1")))));
        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorkbookRelsXml()
    {
        var document = new XDocument(
            new XElement(PackageRelNs + "Relationships",
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml"))));
        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildStylesXml()
    {
        var document = new XDocument(
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
        return document.Declaration + document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildWorksheetXml(IReadOnlyCollection<IReadOnlyCollection<object?>> rows)
    {
        var sheetData = new XElement(SpreadsheetNs + "sheetData");
        var rowIndex = 1u;
        foreach (var row in rows)
        {
            var rowElement = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowIndex));
            var columnIndex = 1;
            foreach (var value in row)
            {
                var cellReference = $"{GetColumnName(columnIndex)}{rowIndex}";
                rowElement.Add(BuildCell(cellReference, value));
                columnIndex++;
            }

            sheetData.Add(rowElement);
            rowIndex++;
        }

        var worksheet = new XDocument(
            new XElement(SpreadsheetNs + "worksheet", sheetData));
        return worksheet.Declaration + worksheet.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildCell(string cellReference, object? value)
    {
        if (value is null)
        {
            return new XElement(SpreadsheetNs + "c", new XAttribute("r", cellReference));
        }

        if (value is DateTime dateTime)
        {
            return BuildCell(cellReference, dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return value switch
        {
            decimal or double or float or int or long or short
                => new XElement(SpreadsheetNs + "c",
                    new XAttribute("r", cellReference),
                    new XElement(SpreadsheetNs + "v", Convert.ToString(value, CultureInfo.InvariantCulture))),
           _ => new XElement(SpreadsheetNs + "c",
               new XAttribute("r", cellReference),
               new XAttribute("t", "inlineStr"),
               new XElement(SpreadsheetNs + "is",
                    new XElement(SpreadsheetNs + "t", Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)))
       };
    }

    private static string GetColumnName(int index)
    {
        var output = string.Empty;
        var current = index;
        while (current > 0)
        {
            current--;
            output = (char)('A' + (current % 26)) + output;
            current /= 26;
        }

        return output;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
