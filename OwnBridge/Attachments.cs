using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace OwnBridge;

// A file the user attached: either a table (Excel/CSV rows) or a document (text/markdown/Word).
internal sealed class Attachment
{
    public required string FilePath { get; init; }
    public List<string> Headers { get; init; } = new();
    public List<List<string>> Rows { get; init; } = new();
    // Where each row lives in the file (Excel row number, or CSV record index), so status can be written back.
    public List<int> RowNumbers { get; init; } = new();
    public string Text { get; init; } = string.Empty;
    public bool IsTable => Headers.Count > 0;

    public string FileName => Path.GetFileName(FilePath);

    public static Attachment Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xlsm" => Spreadsheet.ReadFirstSheet(path),
            ".csv" => ReadCsv(path),
            ".docx" => new Attachment { FilePath = path, Text = WordReader.ReadAsMarkdown(path) },
            ".md" or ".txt" or ".markdown" => new Attachment { FilePath = path, Text = File.ReadAllText(path) },
            ".pdf" => new Attachment { FilePath = path, Text = ReadPdf(path) },
            _ => throw new NotSupportedException($"{extension} files are not supported yet. Use .xlsx, .csv, .docx, .pdf, .md or .txt."),
        };
    }

    private static string ReadPdf(string path)
    {
        string text;
        try { text = PdfText.Extract(path); }
        catch (Exception ex) when (ex is not FileNotFoundException) { text = string.Empty; }
        if (!PdfText.LooksReadable(text))
            throw new InvalidDataException($"No readable text in {Path.GetFileName(path)}. It may be a scanned PDF (pictures of pages). " +
                                           "Export it again as a text PDF, or send a page as a screenshot with Paste image.");
        return text;
    }

    private static Attachment ReadCsv(string path)
    {
        var records = ParseCsv(File.ReadAllText(path)).Select((r, i) => (Row: r, Index: i))
            .Where(r => r.Row.Any(c => c.Length > 0)).ToList();
        if (records.Count == 0) return new Attachment { FilePath = path };
        return new Attachment
        {
            FilePath = path,
            Headers = records[0].Row,
            Rows = records.Skip(1).Select(r => r.Row).ToList(),
            RowNumbers = records.Skip(1).Select(r => r.Index).ToList(),
        };
    }

    // Sets one CSV cell (record index, column) and writes the file back, keeping a UTF-8 BOM if it had one.
    public static void SetCsvCell(string path, int recordIndex, int column, string value)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var records = ParseCsv(File.ReadAllText(path)).ToList();
        if (recordIndex < 0 || recordIndex >= records.Count) throw new InvalidDataException("That row is no longer in the file.");
        var row = records[recordIndex];
        while (row.Count <= column) row.Add(string.Empty);
        row[column] = value;
        var text = new StringBuilder();
        foreach (var record in records)
            text.Append(string.Join(",", record.Select(Quote))).Append("\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(bom));
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;

    // RFC 4180 style: quotes, doubled quotes, commas and newlines inside quotes.
    public static IEnumerable<List<string>> ParseCsv(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim()); cell.Clear();
                yield return row;
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().Trim()); yield return row; }
    }
}

// Reads and writes .xlsx with the built-in zip and XML classes (no extra libraries).
internal static class Spreadsheet
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static Attachment ReadFirstSheet(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var s = sharedEntry.Open();
            foreach (var si in XDocument.Load(s).Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
        }

        var sheetEntry = FirstSheetEntry(zip) ?? throw new InvalidDataException("The workbook has no worksheet.");
        var grid = new List<List<string>>();
        var numbers = new List<int>();
        using (var s = sheetEntry.Open())
        {
            foreach (var row in XDocument.Load(s).Descendants(Main + "row"))
            {
                numbers.Add((int?)row.Attribute("r") ?? numbers.Count + 1);
                var values = new List<string>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = ColumnIndex((string?)cell.Attribute("r")) ?? values.Count;
                    while (values.Count < column) values.Add(string.Empty);
                    values.Add(CellText(cell, shared));
                }
                grid.Add(values);
            }
        }

        var kept = grid.Select((r, i) => (Row: r, Number: numbers[i])).Where(r => r.Row.Any(c => c.Length > 0)).ToList();
        if (kept.Count == 0) return new Attachment { FilePath = path };
        var headers = kept[0].Row.Select((h, i) => h.Length > 0 ? h : $"Column {i + 1}").ToList();
        return new Attachment
        {
            FilePath = path,
            Headers = headers,
            Rows = kept.Skip(1).Select(r => r.Row).ToList(),
            RowNumbers = kept.Skip(1).Select(r => r.Number).ToList(),
        };
    }

    // Writes one text cell into the first worksheet of an existing workbook, keeping everything else
    // (formatting, other sheets, formulas). Fails with IOException while the file is open in Excel.
    public static void SetCell(string path, int rowNumber, int column, string value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
        var entry = FirstSheetEntry(zip) ?? throw new InvalidDataException("The workbook has no worksheet.");
        var name = entry.FullName;
        XDocument document;
        using (var s = entry.Open()) document = XDocument.Load(s);

        var rows = document.Descendants(Main + "row").ToList();
        XElement? row = null;
        for (var i = 0; i < rows.Count; i++)
            if (((int?)rows[i].Attribute("r") ?? i + 1) == rowNumber) { row = rows[i]; break; }
        if (row is null) throw new InvalidDataException($"Row {rowNumber} is no longer in the sheet.");

        var reference = $"{ColumnName(column)}{rowNumber}";
        var cell = row.Elements(Main + "c").FirstOrDefault(c => string.Equals((string?)c.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
        {
            cell = new XElement(Main + "c", new XAttribute("r", reference));
            var after = row.Elements(Main + "c").FirstOrDefault(c => (ColumnIndex((string?)c.Attribute("r")) ?? -1) > column);
            if (after is not null) after.AddBeforeSelf(cell);
            else row.Add(cell);
        }
        cell.Attribute("t")?.Remove();
        cell.Elements().Remove();
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(Main + "is", new XElement(Main + "t", Clean(value))));

        entry.Delete();
        var replacement = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var output = replacement.Open();
        document.Save(output, SaveOptions.DisableFormatting);
    }

    private static ZipArchiveEntry? FirstSheetEntry(ZipArchive zip)
    {
        try
        {
            var workbook = zip.GetEntry("xl/workbook.xml");
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbook is not null && rels is not null)
            {
                string? id;
                using (var s = workbook.Open())
                    id = (string?)XDocument.Load(s).Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Rel + "id");
                if (id is not null)
                {
                    using var s = rels.Open();
                    var target = (string?)XDocument.Load(s).Descendants(PackageRel + "Relationship")
                        .FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target");
                    if (target is not null)
                    {
                        var name = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                        if (zip.GetEntry(name) is { } entry) return entry;
                    }
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // Fall back to the first worksheet file below.
        }
        return zip.GetEntry("xl/worksheets/sheet1.xml") ??
               zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                                      e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string CellText(XElement cell, List<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr") return string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)).Trim();
        var value = cell.Element(Main + "v")?.Value ?? string.Empty;
        return type switch
        {
            "s" when int.TryParse(value, out var index) && index < shared.Count => shared[index].Trim(),
            "b" => value == "1" ? "TRUE" : "FALSE",
            _ => value.Trim(),
        };
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c)) break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return index == 0 ? null : index - 1;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    // Writes a new single-sheet workbook with text cells.
    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sheetRows = new List<XElement>();
        var rowNumber = 1;
        foreach (var values in new[] { headers }.Concat(rows))
        {
            var cells = values.Select((v, i) => new XElement(Main + "c",
                new XAttribute("r", $"{ColumnName(i)}{rowNumber}"), new XAttribute("t", "inlineStr"),
                new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(v)))));
            sheetRows.Add(new XElement(Main + "row", new XAttribute("r", rowNumber), cells));
            rowNumber++;
        }

        var sheet = new XDocument(new XElement(Main + "worksheet", new XElement(Main + "sheetData", sheetRows)));
        var workbook = new XDocument(new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Rel),
            new XElement(Main + "sheets", new XElement(Main + "sheet", new XAttribute("name", "OwnBridge"),
                new XAttribute("sheetId", 1), new XAttribute(Rel + "id", "rId1")))));
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = new XDocument(new XElement(ct + "Types",
            new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
        var rootRels = new XDocument(new XElement(PackageRel + "Relationships",
            new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "xl/workbook.xml"))));
        var workbookRels = new XDocument(new XElement(PackageRel + "Relationships",
            new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", "worksheets/sheet1.xml"))));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Add(string name, XDocument document)
        {
            using var entry = zip.CreateEntry(name).Open();
            document.Save(entry);
        }
        Add("[Content_Types].xml", types);
        Add("_rels/.rels", rootRels);
        Add("xl/workbook.xml", workbook);
        Add("xl/_rels/workbook.xml.rels", workbookRels);
        Add("xl/worksheets/sheet1.xml", sheet);
    }

    // XML cannot hold most control characters; Excel also limits a cell to 32,767 characters.
    private static string Clean(string value)
    {
        var text = new string(value.Where(c => c == '\t' || c == '\n' || c == '\r' || c >= ' ').ToArray());
        return text.Length > 32_000 ? text[..32_000] : text;
    }
}

// Converts a .docx body to markdown-like text: headings become #, lists become -, tables become | rows.
internal static class WordReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static string ReadAsMarkdown(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("This Word file has no document body.");
        using var s = entry.Open();
        var body = XDocument.Load(s).Root?.Element(W + "body");
        if (body is null) return string.Empty;

        var text = new StringBuilder();
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p") text.AppendLine(Paragraph(block));
            else if (block.Name == W + "tbl")
            {
                foreach (var row in block.Elements(W + "tr"))
                    text.AppendLine("| " + string.Join(" | ", row.Elements(W + "tc").Select(c =>
                        string.Join(" ", c.Elements(W + "p").Select(PlainText)))) + " |");
            }
        }
        return text.ToString();
    }

    private static string Paragraph(XElement p)
    {
        var content = PlainText(p);
        var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? string.Empty;
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return "# " + content;
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(style[7..], out var level) && level is > 0 and < 7)
            return new string('#', level) + " " + content;
        if (p.Element(W + "pPr")?.Element(W + "numPr") is not null) return "- " + content;
        return content;
    }

    private static string PlainText(XElement p)
    {
        var text = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            if (node.Name == W + "t") text.Append(node.Value);
            else if (node.Name == W + "tab") text.Append('\t');
            else if (node.Name == W + "br") text.Append('\n');
        }
        return text.ToString();
    }
}
