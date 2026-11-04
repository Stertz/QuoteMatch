using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace QuoteMatch.Testing;

internal static class SyntheticWorkbook
{
    public static byte[] Xlsx(object?[][] rows, string sheetName = "Synthetic", string? codeFormat = null, bool cachedFormula = false, bool macroEnabled = false)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            Entry("[Content_Types].xml", ("""
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """).Replace("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", macroEnabled ? "application/vnd.ms-excel.sheet.macroEnabled.main+xml" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            Entry("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="root" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Entry("xl/workbook.xml", new XElement(ns + "workbook", new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
                new XElement(ns + "sheets", new XElement(ns + "sheet", new XAttribute("name", sheetName), new XAttribute("sheetId", 1),
                    new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), "sheet1")))).ToString());
            Entry("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="sheet1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="styles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Entry("xl/styles.xml", new XElement(ns + "styleSheet",
                new XElement(ns + "numFmts", new XAttribute("count", 1), new XElement(ns + "numFmt", new XAttribute("numFmtId", 164), new XAttribute("formatCode", codeFormat ?? "General"))),
                new XElement(ns + "cellXfs", new XAttribute("count", 2), new XElement(ns + "xf", new XAttribute("numFmtId", 0)), new XElement(ns + "xf", new XAttribute("numFmtId", 164)))).ToString());
            XElement sheetData = new(ns + "sheetData");
            for (int row = 0; row < rows.Length; row++)
            {
                XElement line = new(ns + "row", new XAttribute("r", row + 1));
                for (int column = 0; column < rows[row].Length; column++)
                {
                    object? value = rows[row][column];
                    if (value is null) continue;
                    XElement cell = new(ns + "c", new XAttribute("r", QuoteMatch.Application.Importing.SpreadsheetExtraction.ColumnName(column) + (row + 1)));
                    if (value is string text) { cell.Add(new XAttribute("t", "inlineStr")); cell.Add(new XElement(ns + "is", new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))); }
                    else
                    {
                        if (column == 0 && codeFormat is not null) cell.Add(new XAttribute("s", 1));
                        if (cachedFormula && row > 0 && column == 4) cell.Add(new XElement(ns + "f", $"C{row + 1}*D{row + 1}"));
                        cell.Add(new XElement(ns + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
                    }
                    line.Add(cell);
                }
                sheetData.Add(line);
            }
            Entry("xl/worksheets/sheet1.xml", new XElement(ns + "worksheet", sheetData).ToString());
            void Entry(string name, string content) { using StreamWriter writer = new(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(content); }
        }
        return stream.ToArray();
    }

    public static byte[] Xls(object?[][] rows)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, true);
        Record(0x0009, data => { data.Write((ushort)2); data.Write((ushort)0x10); });
        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < rows[row].Length; column++)
            {
                object? value = rows[row][column];
                if (value is null) continue;
                Record(value is string ? (ushort)0x0004 : (ushort)0x0003, data =>
                {
                    data.Write((ushort)row); data.Write((ushort)column); data.Write(new byte[3]);
                    if (value is string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); data.Write((byte)bytes.Length); data.Write(bytes); }
                    else data.Write(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                });
            }
        }
        Record(0x000A, _ => { });
        return stream.ToArray();
        void Record(ushort type, Action<BinaryWriter> write)
        {
            using MemoryStream content = new(); using BinaryWriter data = new(content); write(data);
            writer.Write(type); writer.Write((ushort)content.Length); writer.Write(content.ToArray());
        }
    }

    public static byte[] OpenDocument(string tables, bool flat = false)
    {
        string root = flat ? "document" : "document-content";
        string content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:{root} xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:calcext="urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0" xmlns:of="urn:oasis:names:tc:opendocument:xmlns:of:1.2" office:version="1.3">
              <office:body><office:spreadsheet>{tables}</office:spreadsheet></office:body>
            </office:{root}>
            """;
        if (flat) return Encoding.UTF8.GetBytes(content);
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false))) writer.Write("application/vnd.oasis.opendocument.spreadsheet");
            using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open(), new UTF8Encoding(false))) writer.Write(content);
            using var manifest = new StreamWriter(zip.CreateEntry("META-INF/manifest.xml").Open());
            manifest.Write("""
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/></manifest:manifest>
                """);
        }
        return stream.ToArray();
    }

    public static byte[] Xlsb(object?[][] rows)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            using (StreamWriter writer = new(zip.CreateEntry("[Content_Types].xml").Open())) writer.Write("""
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="bin" ContentType="application/vnd.ms-excel.worksheet"/><Override PartName="/xl/workbook.bin" ContentType="application/vnd.ms-excel.sheet.binary.macroEnabled.main"/></Types>
                """);
            using (StreamWriter writer = new(zip.CreateEntry("xl/_rels/workbook.bin.rels").Open())) writer.Write("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="sheet1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.bin"/></Relationships>
                """);
            using (BinaryWriter workbook = new(zip.CreateEntry("xl/workbook.bin").Open()))
            {
                Record(workbook, 0x83, _ => { }); Record(workbook, 0x8F, _ => { });
                Record(workbook, 0x9C, data => { data.Write(0); data.Write(1); Wide(data, "sheet1"); Wide(data, "Synthetic"); });
                Record(workbook, 0x90, _ => { }); Record(workbook, 0x84, _ => { });
            }
            using BinaryWriter sheet = new(zip.CreateEntry("xl/worksheets/sheet1.bin").Open());
            Record(sheet, 0x81, _ => { });
            Record(sheet, 0x94, data => { data.Write(0); data.Write(rows.Length - 1); data.Write(0); data.Write(rows.Max(row => row.Length) - 1); });
            Record(sheet, 0x91, _ => { });
            for (int row = 0; row < rows.Length; row++)
            {
                Record(sheet, 0, data => { data.Write(row); data.Write(new byte[13]); });
                for (int column = 0; column < rows[row].Length; column++)
                {
                    object? value = rows[row][column]; if (value is null) continue;
                    Record(sheet, value is string ? 6u : 5u, data =>
                    {
                        data.Write(column); data.Write(0);
                        if (value is string text) Wide(data, text); else data.Write(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    });
                }
            }
            Record(sheet, 0x92, _ => { }); Record(sheet, 0x82, _ => { });
        }
        return stream.ToArray();
        static void Wide(BinaryWriter writer, string text) { writer.Write(text.Length); writer.Write(Encoding.Unicode.GetBytes(text)); }
        static void Variable(BinaryWriter writer, uint value)
        {
            do { byte part = (byte)(value & 127); value >>= 7; writer.Write((byte)(part | (value > 0 ? 128 : 0))); } while (value > 0);
        }
        static void Record(BinaryWriter writer, uint type, Action<BinaryWriter> write)
        {
            using MemoryStream content = new(); using BinaryWriter data = new(content); write(data);
            Variable(writer, type); Variable(writer, (uint)content.Length); writer.Write(content.ToArray());
        }
    }
}
