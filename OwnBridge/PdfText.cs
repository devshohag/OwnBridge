using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace OwnBridge;

// Extracts the text of a PDF with no extra libraries: objects (including compressed object streams),
// the page tree, Flate-compressed content streams, and fonts (WinAnsi, or a ToUnicode map for embedded
// fonts such as Word's and browsers'). Scanned PDFs have no text; Extract then returns an empty string.
internal static class PdfText
{
    public const int MaxPages = 300;

    public static string Extract(string path) => new Reader(File.ReadAllBytes(path)).ReadText();

    private sealed class Reader
    {
        private readonly byte[] data;
        private readonly Dictionary<int, string> objects = new();        // object number → dictionary text
        private readonly Dictionary<int, byte[]?> streams = new();       // object number → decoded stream
        private readonly Dictionary<int, (int Offset, int Length)> rawStreams = new();
        private readonly Dictionary<int, FontMap> fonts = new();

        public Reader(byte[] data)
        {
            this.data = data;
            IndexObjects();
        }

        public string ReadText()
        {
            var pages = PageObjects();
            var text = new StringBuilder();
            if (pages.Count > 0)
            {
                foreach (var page in pages.Take(MaxPages))
                {
                    var fontsOnPage = FontsOf(page);
                    foreach (var content in ContentsOf(page))
                        if (Stream(content) is { } bytes) text.Append(new ContentParser(bytes, fontsOnPage).Run());
                    text.Append("\n\n");
                }
            }
            else
            {
                // No readable page tree: fall back to every stream that draws text, in file order.
                foreach (var number in rawStreams.Keys.OrderBy(n => n))
                    if (Stream(number) is { } bytes && Contains(bytes, "BT"))
                        text.Append(new ContentParser(bytes, new Dictionary<string, FontMap>()).Run()).Append("\n\n");
            }
            return Tidy(text.ToString());
        }

        // ---- objects ----

        private static readonly Regex ObjectStart = new(@"(\d+)\s+(\d+)\s+obj\b", RegexOptions.Compiled);

        private void IndexObjects()
        {
            var latin = Encoding.Latin1.GetString(data);
            var insideStreamUntil = -1;
            foreach (Match match in ObjectStart.Matches(latin))
            {
                if (match.Index < insideStreamUntil) continue; // "12 0 obj" inside compressed bytes is not an object.
                var number = int.Parse(match.Groups[1].Value);
                var bodyStart = match.Index + match.Length;
                var end = latin.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (end < 0) end = latin.Length;
                var streamAt = latin.IndexOf("stream", bodyStart, StringComparison.Ordinal);
                if (streamAt >= 0 && streamAt < end && !IsEndStream(latin, streamAt))
                {
                    objects[number] = latin[bodyStart..streamAt];
                    var start = streamAt + 6;
                    if (start < latin.Length && latin[start] == '\r') start++;
                    if (start < latin.Length && latin[start] == '\n') start++;
                    var length = DeclaredLength(objects[number], latin, start);
                    var stop = latin.IndexOf("endstream", start, StringComparison.Ordinal);
                    if (length is null || start + length > data.Length) length = (stop < 0 ? end : stop) - start;
                    rawStreams[number] = (start, Math.Max(0, length.Value));
                    insideStreamUntil = start + Math.Max(0, length.Value);
                }
                else objects[number] = latin[bodyStart..end];
            }
            // Objects packed inside compressed object streams (PDF 1.5+).
            foreach (var number in rawStreams.Keys.ToList())
            {
                if (!objects[number].Contains("/ObjStm", StringComparison.Ordinal)) continue;
                if (Stream(number) is not { } packed) continue;
                var first = IntValue(objects[number], "First");
                var count = IntValue(objects[number], "N");
                if (first is null || count is null) continue;
                var text = Encoding.Latin1.GetString(packed);
                var header = text[..Math.Min(first.Value, text.Length)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i + 1 < header.Length && i / 2 < count; i += 2)
                {
                    if (!int.TryParse(header[i], out var inner) || !int.TryParse(header[i + 1], out var offset)) continue;
                    var from = first.Value + offset;
                    var to = i + 3 < header.Length && int.TryParse(header[i + 3], out var next) ? first.Value + next : text.Length;
                    if (from < text.Length && to <= text.Length && to > from && !objects.ContainsKey(inner))
                        objects[inner] = text[from..to];
                }
            }
        }

        private static bool IsEndStream(string text, int at) => at >= 3 && string.CompareOrdinal(text, at - 3, "end", 0, 3) == 0;

        private int? DeclaredLength(string dictionary, string latin, int start)
        {
            var match = Regex.Match(dictionary, @"/Length\s+(\d+)(\s+(\d+)\s+R)?");
            if (!match.Success) return null;
            if (!match.Groups[2].Success) return int.Parse(match.Groups[1].Value);
            var referenced = int.Parse(match.Groups[1].Value);
            // The referenced object may not be indexed yet; read it directly.
            var m = new Regex($@"\b{referenced}\s+\d+\s+obj\s+(\d+)").Match(latin);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        private byte[]? Stream(int number)
        {
            if (streams.TryGetValue(number, out var cached)) return cached;
            byte[]? result = null;
            if (rawStreams.TryGetValue(number, out var raw))
            {
                var bytes = data.AsSpan(raw.Offset, Math.Min(raw.Length, data.Length - raw.Offset)).ToArray();
                result = Decode(bytes, Filters(objects[number]));
            }
            streams[number] = result;
            return result;
        }

        // "/Filter /FlateDecode" or "/Filter [ /ASCII85Decode /FlateDecode ]" → the names in order.
        private static List<string> Filters(string dictionary)
        {
            var match = Regex.Match(dictionary, @"/Filter\s*(\[([^\]]*)\]|/(\w+))");
            if (!match.Success) return new List<string>();
            if (match.Groups[3].Success) return new List<string> { match.Groups[3].Value };
            return Regex.Matches(match.Groups[2].Value, @"/(\w+)").Select(m => m.Groups[1].Value).ToList();
        }

        // Applies the filters in order; null when one of them is not a text filter (images and so on).
        private static byte[]? Decode(byte[] bytes, List<string> filters)
        {
            byte[]? current = bytes;
            foreach (var filter in filters)
            {
                if (current is null) return null;
                current = filter switch
                {
                    "FlateDecode" or "Fl" => Inflate(current),
                    "ASCII85Decode" or "A85" => Ascii85(current),
                    "ASCIIHexDecode" or "AHx" => AsciiHex(current),
                    _ => null,
                };
            }
            return current;
        }

        private static byte[] Ascii85(byte[] input)
        {
            var output = new List<byte>(input.Length);
            var group = new int[5];
            var count = 0;
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c == '~') break;
                if (c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0) continue;
                if (c == 'z' && count == 0) { output.AddRange(new byte[4]); continue; }
                if (c < '!' || c > 'u') continue;
                group[count++] = c - '!';
                if (count == 5)
                {
                    uint value = 0;
                    for (var k = 0; k < 5; k++) value = value * 85 + (uint)group[k];
                    output.Add((byte)(value >> 24)); output.Add((byte)(value >> 16)); output.Add((byte)(value >> 8)); output.Add((byte)value);
                    count = 0;
                }
            }
            if (count > 1)
            {
                for (var k = count; k < 5; k++) group[k] = 84;
                uint value = 0;
                for (var k = 0; k < 5; k++) value = value * 85 + (uint)group[k];
                var tail = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
                output.AddRange(tail.Take(count - 1));
            }
            return output.ToArray();
        }

        private static byte[] AsciiHex(byte[] input)
        {
            var hex = new StringBuilder();
            foreach (var b in input)
            {
                if (b == '>') break;
                if (Uri.IsHexDigit((char)b)) hex.Append((char)b);
            }
            if (hex.Length % 2 == 1) hex.Append('0');
            return Convert.FromHexString(hex.ToString());
        }

        private static byte[]? Inflate(byte[] bytes)
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // Some writers omit the zlib header: try raw deflate after skipping two bytes.
                try
                {
                    using var input = new MemoryStream(bytes, 2, Math.Max(0, bytes.Length - 2));
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }
        }

        private string Dict(int number) => objects.TryGetValue(number, out var text) ? text : string.Empty;

        private static int? IntValue(string dictionary, string key)
        {
            var match = Regex.Match(dictionary, $@"/{key}\s+(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        private static int? Ref(string dictionary, string key)
        {
            var match = Regex.Match(dictionary, $@"/{key}\s+(\d+)\s+\d+\s+R");
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }

        private static List<int> RefArray(string dictionary, string key)
        {
            var match = Regex.Match(dictionary, $@"/{key}\s*\[([^\]]*)\]");
            if (!match.Success) return Ref(dictionary, key) is { } single ? new List<int> { single } : new List<int>();
            return Regex.Matches(match.Groups[1].Value, @"(\d+)\s+\d+\s+R").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        }

        // ---- pages ----

        private List<int> PageObjects()
        {
            var pages = new List<int>();
            var catalog = objects.FirstOrDefault(o => Regex.IsMatch(o.Value, @"/Type\s*/Catalog")).Key;
            var root = catalog != 0 ? Ref(Dict(catalog), "Pages") : null;
            if (root is null)
            {
                // No catalog found: take page objects in number order.
                return objects.Where(o => Regex.IsMatch(o.Value, @"/Type\s*/Page(?!s)")).Select(o => o.Key).OrderBy(n => n).ToList();
            }
            var visited = new HashSet<int>();
            void Walk(int node)
            {
                if (!visited.Add(node) || pages.Count > MaxPages) return;
                var dictionary = Dict(node);
                if (Regex.IsMatch(dictionary, @"/Type\s*/Pages")) foreach (var kid in RefArray(dictionary, "Kids")) Walk(kid);
                else if (Regex.IsMatch(dictionary, @"/Type\s*/Page")) pages.Add(node);
            }
            Walk(root.Value);
            return pages;
        }

        private List<int> ContentsOf(int page) => RefArray(Dict(page), "Contents");

        // Font resource names (/F1) on a page → decoding maps. Resources may be inherited from a parent.
        private Dictionary<string, FontMap> FontsOf(int page)
        {
            var result = new Dictionary<string, FontMap>(StringComparer.Ordinal);
            var node = (int?)page;
            var guard = 0;
            while (node is not null && guard++ < 20)
            {
                var dictionary = Dict(node.Value);
                var resources = ResolveInline(dictionary, "Resources");
                if (resources is not null)
                {
                    var fontDict = ResolveInline(resources, "Font");
                    if (fontDict is not null)
                    {
                        foreach (Match m in Regex.Matches(fontDict, @"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R"))
                            result.TryAdd(m.Groups[1].Value, FontFor(int.Parse(m.Groups[2].Value)));
                        return result;
                    }
                }
                node = Ref(dictionary, "Parent");
            }
            return result;
        }

        // The value of /Key as dictionary text, following an indirect reference if needed.
        private string? ResolveInline(string dictionary, string key)
        {
            var at = Regex.Match(dictionary, $@"/{key}\s*(<<|(\d+)\s+\d+\s+R)");
            if (!at.Success) return null;
            if (at.Groups[2].Success) return Dict(int.Parse(at.Groups[2].Value));
            var start = at.Index + at.Length - 2;
            var depth = 0;
            for (var i = start; i < dictionary.Length - 1; i++)
            {
                if (dictionary[i] == '<' && dictionary[i + 1] == '<') { depth++; i++; }
                else if (dictionary[i] == '>' && dictionary[i + 1] == '>')
                {
                    depth--;
                    i++;
                    if (depth == 0) return dictionary[start..(i + 1)];
                }
            }
            return dictionary[start..];
        }

        private FontMap FontFor(int number)
        {
            if (fonts.TryGetValue(number, out var known)) return known;
            var dictionary = Dict(number);
            var map = new FontMap { TwoByte = dictionary.Contains("/Identity-H", StringComparison.Ordinal) || dictionary.Contains("/Type0", StringComparison.Ordinal) };
            if (Ref(dictionary, "ToUnicode") is { } cmap && Stream(cmap) is { } bytes) map.Load(Encoding.Latin1.GetString(bytes));
            fonts[number] = map;
            return map;
        }

        private static bool Contains(byte[] bytes, string token) =>
            Encoding.Latin1.GetString(bytes).Contains(token, StringComparison.Ordinal);
    }

    // How a font's byte codes become Unicode text.
    private sealed class FontMap
    {
        public bool TwoByte { get; set; }
        private readonly Dictionary<int, string> map = new();
        private int codeBytes;

        public void Load(string cmap)
        {
            foreach (Match block in Regex.Matches(cmap, @"begincodespacerange(.*?)endcodespacerange", RegexOptions.Singleline))
            {
                var first = Regex.Match(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>");
                if (first.Success) codeBytes = first.Groups[1].Value.Length / 2;
            }
            foreach (Match block in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match pair in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>"))
                {
                    map[Convert.ToInt32(pair.Groups[1].Value, 16)] = Utf16(pair.Groups[2].Value);
                    if (codeBytes == 0) codeBytes = pair.Groups[1].Value.Length / 2;
                }
            foreach (Match block in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
            {
                foreach (Match range in Regex.Matches(block.Groups[1].Value,
                             @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<([0-9A-Fa-f]*)>|\[([^\]]*)\])"))
                {
                    var low = Convert.ToInt32(range.Groups[1].Value, 16);
                    var high = Convert.ToInt32(range.Groups[2].Value, 16);
                    if (high - low > 65535 || high < low) continue;
                    if (codeBytes == 0) codeBytes = range.Groups[1].Value.Length / 2;
                    if (range.Groups[4].Success)
                    {
                        var start = range.Groups[4].Value;
                        if (start.Length == 0) continue;
                        var baseValue = Convert.ToInt32(start[^Math.Min(4, start.Length)..], 16);
                        var prefix = Utf16(start[..^Math.Min(4, start.Length)]);
                        for (var code = low; code <= high; code++)
                        {
                            var point = baseValue + code - low;
                            map[code] = prefix + (point is >= 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF) ? ((char)point).ToString() : "\uFFFD");
                        }
                    }
                    else
                    {
                        var items = Regex.Matches(range.Groups[5].Value, @"<([0-9A-Fa-f]*)>").Select(m => m.Groups[1].Value).ToList();
                        for (var i = 0; i < items.Count && low + i <= high; i++) map[low + i] = Utf16(items[i]);
                    }
                }
            }
        }

        public string Decode(byte[] bytes)
        {
            var text = new StringBuilder();
            var width = codeBytes > 0 ? codeBytes : TwoByte ? 2 : 1;
            if (map.Count == 0 && width == 1)
            {
                foreach (var b in bytes) text.Append(WinAnsi(b));
                return text.ToString();
            }
            for (var i = 0; i + width <= bytes.Length; i += width)
            {
                var code = 0;
                for (var k = 0; k < width; k++) code = (code << 8) | bytes[i + k];
                if (map.TryGetValue(code, out var value)) text.Append(value);
                else if (width == 1) text.Append(WinAnsi((byte)code));
            }
            return text.ToString();
        }

        private static string Utf16(string hex)
        {
            if (hex.Length < 4) return hex.Length == 0 ? string.Empty : ((char)Convert.ToInt32(hex, 16)).ToString();
            var chars = new char[hex.Length / 4];
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)Convert.ToInt32(hex.Substring(i * 4, 4), 16);
            return new string(chars);
        }

        // Windows-1252: 0x80–0x9F hold quotes, dashes and bullets; the rest matches Latin-1.
        private static readonly string Cp1252High = "€�‚ƒ„…†‡ˆ‰Š‹Œ�Ž��‘’“”•–—˜™š›œ�žŸ";

        private static char WinAnsi(byte b) => b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;
    }

    // Walks a content stream: text operators produce text; moves to a new line produce line breaks.
    private sealed class ContentParser
    {
        private readonly byte[] s;
        private readonly Dictionary<string, FontMap> fonts;
        private readonly StringBuilder output = new();
        private readonly List<object> operands = new();
        private FontMap current = new();
        private int pos;

        public ContentParser(byte[] content, Dictionary<string, FontMap> fonts)
        {
            s = content;
            this.fonts = fonts;
        }

        public string Run()
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (IsWhite(c)) { pos++; continue; }
                if (c == '%') { while (pos < s.Length && s[pos] != '\n' && s[pos] != '\r') pos++; continue; }
                if (c == '(') { operands.Add(ReadLiteral()); continue; }
                if (c == '<' && pos + 1 < s.Length && s[pos + 1] == '<') { SkipDictionary(); continue; }
                if (c == '<') { operands.Add(ReadHex()); continue; }
                if (c == '[') { pos++; operands.Add(ReadArray()); continue; }
                if (c == ']') { pos++; continue; }
                if (c == '/') { operands.Add("/" + ReadWord(1)); continue; }
                var word = ReadWord(0);
                if (word.Length == 0) { pos++; continue; }
                if (double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    operands.Add(number);
                else
                {
                    Operator(word);
                    operands.Clear();
                }
            }
            return output.ToString();
        }

        // Text position (translation part of the text line matrix), the font size and the leading.
        private double lineX, lineY, fontSize = 10, leading;
        private double shownX = double.NaN, shownY = double.NaN;
        private bool newBlock;
        // Current transformation matrix [a b c d e f] with its q/Q stack; positions are compared on the page.
        private double[] ctm = { 1, 0, 0, 1, 0, 0 };
        private readonly Stack<double[]> saved = new();

        private void Operator(string op)
        {
            switch (op)
            {
                case "Tf":
                    if (operands.Count >= 2 && operands[^2] is string name && fonts.TryGetValue(name.TrimStart('/'), out var font)) current = font;
                    else if (operands.Count >= 2) current = new FontMap();
                    if (operands.Count >= 1 && operands[^1] is double size && Math.Abs(size) > 0.01) fontSize = Math.Abs(size);
                    break;
                case "TL":
                    if (operands.LastOrDefault() is double tl) leading = tl;
                    break;
                case "BT":
                    lineX = 0;
                    lineY = 0;
                    newBlock = true;
                    break;
                case "q":
                    saved.Push((double[])ctm.Clone());
                    break;
                case "Q":
                    if (saved.Count > 0) ctm = saved.Pop();
                    break;
                case "cm":
                    if (operands.Count >= 6 && operands.Skip(operands.Count - 6).All(o => o is double))
                    {
                        var m = operands.Skip(operands.Count - 6).Cast<double>().ToArray();
                        ctm = new[]
                        {
                            m[0] * ctm[0] + m[1] * ctm[2], m[0] * ctm[1] + m[1] * ctm[3],
                            m[2] * ctm[0] + m[3] * ctm[2], m[2] * ctm[1] + m[3] * ctm[3],
                            m[4] * ctm[0] + m[5] * ctm[2] + ctm[4], m[4] * ctm[1] + m[5] * ctm[3] + ctm[5],
                        };
                    }
                    break;
                case "Td":
                case "TD":
                    if (operands.Count >= 2 && operands[^1] is double ty && operands[^2] is double tx)
                    {
                        lineX += tx;
                        lineY += ty;
                        if (op == "TD") leading = -ty;
                    }
                    break;
                case "Tm":
                    if (operands.Count >= 6 && operands[^1] is double f && operands[^2] is double e)
                    {
                        lineX = e;
                        lineY = f;
                    }
                    break;
                case "T*":
                    lineY -= leading == 0 ? fontSize * 1.2 : leading;
                    lineX = 0;
                    break;
                case "Tj":
                    if (operands.LastOrDefault() is byte[] text) Show(current.Decode(text));
                    break;
                case "'":
                case "\"":
                    lineY -= leading == 0 ? fontSize * 1.2 : leading;
                    if (operands.LastOrDefault() is byte[] quoted) Show(current.Decode(quoted));
                    break;
                case "TJ":
                    if (operands.LastOrDefault() is List<object> parts)
                    {
                        var first = true;
                        foreach (var part in parts)
                        {
                            if (part is byte[] piece)
                            {
                                if (first) { Show(current.Decode(piece)); first = false; }
                                else Append(current.Decode(piece));
                            }
                            else if (part is double kern && kern < -180) Space();
                        }
                    }
                    break;
            }
        }

        // Starts a new line when the text moved up or down; a space when it jumped well to the right.
        // Browsers place each glyph with its own small move, so small steps add nothing.
        private void Show(string text)
        {
            var x = ctm[0] * lineX + ctm[2] * lineY + ctm[4];
            var y = ctm[1] * lineX + ctm[3] * lineY + ctm[5];
            var size = fontSize * Math.Max(0.01, Math.Sqrt(ctm[2] * ctm[2] + ctm[3] * ctm[3]));
            if (!double.IsNaN(shownY))
            {
                // A new text block a little to the right (a bullet, a bold word) gets a space; inside one
                // block only a large jump does, because browsers move glyph by glyph.
                var gap = newBlock ? size * 0.3 : size * 1.5;
                if (Math.Abs(y - shownY) > Math.Max(0.5, size * 0.4)) NewLine();
                else if (x - shownX > gap || x < shownX - size * 0.5) Space();
            }
            shownX = x;
            shownY = y;
            newBlock = false;
            Append(text);
        }

        private void Append(string text)
        {
            if (text.Length > 0) output.Append(text);
        }

        private void Space()
        {
            if (output.Length > 0 && output[^1] != ' ' && output[^1] != '\n') output.Append(' ');
        }

        private void NewLine()
        {
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
        }

        private static bool IsWhite(byte c) => c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0;

        private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%' or (byte)'{' or (byte)'}';

        private string ReadWord(int skip)
        {
            pos += skip;
            var start = pos;
            while (pos < s.Length && !IsWhite(s[pos]) && !IsDelimiter(s[pos])) pos++;
            return Encoding.Latin1.GetString(s, start, pos - start);
        }

        private byte[] ReadLiteral()
        {
            pos++; // (
            var bytes = new List<byte>();
            var depth = 1;
            while (pos < s.Length)
            {
                var c = s[pos++];
                if (c == '\\' && pos < s.Length)
                {
                    var e = s[pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add((byte)'\n'); break;
                        case (byte)'r': bytes.Add((byte)'\r'); break;
                        case (byte)'t': bytes.Add((byte)'\t'); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case (byte)'\r': if (pos < s.Length && s[pos] == '\n') pos++; break;
                        case (byte)'\n': break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                var value = e - '0';
                                for (var k = 0; k < 2 && pos < s.Length && s[pos] >= '0' && s[pos] <= '7'; k++) value = value * 8 + (s[pos++] - '0');
                                bytes.Add((byte)value);
                            }
                            else bytes.Add(e);
                            break;
                    }
                }
                else if (c == '(') { depth++; bytes.Add(c); }
                else if (c == ')') { if (--depth == 0) break; bytes.Add(c); }
                else bytes.Add(c);
            }
            return bytes.ToArray();
        }

        private byte[] ReadHex()
        {
            pos++; // <
            var hex = new StringBuilder();
            while (pos < s.Length && s[pos] != '>')
            {
                var c = (char)s[pos++];
                if (Uri.IsHexDigit(c)) hex.Append(c);
            }
            pos++; // >
            if (hex.Length % 2 == 1) hex.Append('0');
            return Convert.FromHexString(hex.ToString());
        }

        private List<object> ReadArray()
        {
            var items = new List<object>();
            while (pos < s.Length)
            {
                var c = s[pos];
                if (IsWhite(c)) { pos++; continue; }
                if (c == ']') { pos++; break; }
                if (c == '(') { items.Add(ReadLiteral()); continue; }
                if (c == '<') { items.Add(ReadHex()); continue; }
                var word = ReadWord(0);
                if (word.Length == 0) { pos++; continue; }
                if (double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) items.Add(n);
            }
            return items;
        }

        private void SkipDictionary()
        {
            var depth = 0;
            while (pos + 1 < s.Length)
            {
                if (s[pos] == '<' && s[pos + 1] == '<') { depth++; pos += 2; }
                else if (s[pos] == '>' && s[pos + 1] == '>') { depth--; pos += 2; if (depth == 0) return; }
                else pos++;
            }
            pos = s.Length;
        }
    }

    private static string Tidy(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => Regex.Replace(l, @"[ \t]+", " ").TrimEnd());
        var result = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n");
        return result.Replace("\0", "").Trim();
    }

    // True when the text looks like real words; scanned or oddly encoded PDFs fail this.
    public static bool LooksReadable(string text)
    {
        if (text.Length < 20) return false;
        var letters = text.Count(char.IsLetter);
        var odd = text.Count(c => c == '�' || (char.IsControl(c) && c != '\n' && c != '\t'));
        return letters >= text.Length * 0.3 && odd <= text.Length * 0.02;
    }
}
