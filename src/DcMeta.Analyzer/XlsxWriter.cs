using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace DcMeta.Analyzer;

/// <summary>A worksheet: header row + data rows. Cells may be string, int, double, bool or null.</summary>
public sealed class Sheet
{
    public Sheet(string name, params string[] headers)
    {
        if (name.Length > 31) throw new ArgumentException($"Sheet name '{name}' exceeds Excel's 31-character limit.");
        Name = name;
        Headers = headers;
    }

    public string Name { get; }
    public string[] Headers { get; }
    public List<object?[]> Rows { get; } = new();

    public Sheet Add(params object?[] cells) { Rows.Add(cells); return this; }
}

/// <summary>
/// Dependency-free .xlsx writer (Office Open XML SpreadsheetML). Bold, frozen header row,
/// auto-filter, estimated column widths, numbers stored as numbers. Opens in Excel, LibreOffice
/// and Google Sheets.
/// </summary>
public static class XlsxWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Entry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            string.Concat(sheets.Select((_, i) =>
                $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            "</Types>");

        Entry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        Entry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets>" +
            string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
            "</sheets>" +
            "<definedNames>" +
            string.Concat(sheets.Select((s, i) => s.Rows.Count == 0 ? "" :
                $"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Esc(s.Name.Replace("'", "''"))}'!$A$1:${Col(s.Headers.Length - 1)}${s.Rows.Count + 1}</definedName>")) +
            "</definedNames></workbook>");

        Entry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(sheets.Select((_, i) =>
                $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>");

        // Styles: 0 = default, 1 = bold header with fill, 2 = number 0.000
        Entry(zip, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.000\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDE5F0\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"3\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>");

        for (int i = 0; i < sheets.Count; i++)
            Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    static string SheetXml(Sheet s)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        sb.Append("<cols>");
        for (int c = 0; c < s.Headers.Length; c++)
        {
            int len = s.Headers[c].Length;
            foreach (var row in s.Rows.Take(500))
                if (c < row.Length) len = Math.Max(len, Display(row[c]).Length);
            double width = Math.Clamp(len + 2, 8, 70);
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width.ToString(Inv)}\" customWidth=\"1\"/>");
        }
        sb.Append("</cols><sheetData>");

        AppendRow(sb, 1, s.Headers, header: true);
        for (int r = 0; r < s.Rows.Count; r++) AppendRow(sb, r + 2, s.Rows[r], header: false);
        sb.Append("</sheetData>");
        if (s.Rows.Count > 0)
            sb.Append($"<autoFilter ref=\"A1:{Col(s.Headers.Length - 1)}{s.Rows.Count + 1}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    static void AppendRow(StringBuilder sb, int rowNum, object?[] cells, bool header)
    {
        sb.Append($"<row r=\"{rowNum}\">");
        for (int c = 0; c < cells.Length; c++)
        {
            string r = $"{Col(c)}{rowNum}";
            object? v = cells[c];
            string style = header ? " s=\"1\"" : "";
            switch (v)
            {
                case null:
                    break;
                case double d when double.IsNaN(d) || double.IsInfinity(d):
                    break;
                case double d:
                    sb.Append($"<c r=\"{r}\"{(header ? style : " s=\"2\"")}><v>{d.ToString("R", Inv)}</v></c>");
                    break;
                case int or long or short:
                    sb.Append($"<c r=\"{r}\"{style}><v>{Convert.ToString(v, Inv)}</v></c>");
                    break;
                case bool b:
                    sb.Append($"<c r=\"{r}\"{style} t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                    break;
                default:
                    string text = v.ToString() ?? "";
                    if (text.Length > 32767) text = text[..32767]; // Excel cell limit
                    sb.Append($"<c r=\"{r}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    static string Display(object? v) => v switch
    {
        null => "",
        double d => d.ToString("0.000", Inv),
        _ => v.ToString() ?? "",
    };

    public static string Col(int index)
    {
        var s = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    static string Esc(string s)
    {
        // Strip characters XML 1.0 forbids, then escape markup.
        var clean = new StringBuilder(s.Length);
        foreach (char ch in s)
            if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20) clean.Append(ch);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    static void Entry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
