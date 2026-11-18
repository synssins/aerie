using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kb.Web.Import;

/// <summary>A picture found in or as a file. Name: unique within its import item.</summary>
public sealed record ExtractedImage(string Name, byte[] Data, string Mime);

/// <summary>A file inside another (a zip entry, a mail attachment). Path: where it was inside.</summary>
public sealed record ExtractedFile(string Path, byte[] Data);

/// <summary>Skipped: a system file (Thumbs.db, desktop.ini, an Office lock file) that is no one's content.</summary>
public enum ExtractKind { Text, Image, Container, Blocked, Unsupported, Skipped }

/// <summary>What a file holds: its text (as Markdown-like lines), its pictures, or the files inside it.</summary>
public sealed record Extraction(ExtractKind Kind, string Text, IReadOnlyList<ExtractedImage> Images, IReadOnlyList<ExtractedFile> Children, string? Note = null)
{
    public static Extraction Of(string text, IReadOnlyList<ExtractedImage>? images = null, string? note = null) => new(ExtractKind.Text, text, images ?? [], [], note);
    public static Extraction Blocked(string why) => new(ExtractKind.Blocked, "", [], [], why);
    public static Extraction Unsupported(string why) => new(ExtractKind.Unsupported, "", [], [], why);
    public static Extraction Skipped(string why) => new(ExtractKind.Skipped, "", [], [], why);
}

/// <summary>
/// Reads the text of the formats an import takes. Executables are blocked, never read. A file that cannot be read keeps its
/// original as an attachment and says why. Every reader is bounded (rows, entries, sizes) so one file cannot exhaust the server.
/// </summary>
public static partial class Extractors
{
    public const int MaxZipEntries = 2000;
    public const long MaxZipBytes = 512L * 1024 * 1024;
    public const int MaxDepth = 5;
    /// <summary>The most one XML or HTML part inside an office file, an e-book or a diagram may unpack to.</summary>
    public const long MaxPartBytes = 64L * 1024 * 1024;
    /// <summary>The most one picture inside a document may unpack to; a larger one is left out.</summary>
    public const long MaxMediaBytes = 20L * 1024 * 1024;
    /// <summary>The most HTML that is turned into text.</summary>
    public const int MaxHtmlChars = 20_000_000;
    /// <summary>How long one search of a pattern over a file's text may take.</summary>
    private const int PatternMs = 2000;
    public const string NotRead = "This kind of file is not read; it is kept as an attachment.";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Known = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the app has a reader of its own for a file of this name's extension (or blocks it as a program). It asks the readers
    /// themselves, with a few bytes that are neither text nor any format: only an extension none of them takes answers "not read".
    /// </summary>
    public static bool Reads(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return Known.GetOrAdd(ext, e => Read("x" + e, [0, 0xFF, 0, 0xFE, 1, 2, 3, 0]).Note != NotRead);
    }
    private const int MaxRows = 2000;

    private static readonly HashSet<string> BlockedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".dll", ".sys", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".jar", ".sh", ".reg", ".msc", ".app", ".appx", ".msix", ".ocx", ".drv", ".scf", ".inf", ".gadget",
    };

    static Extractors() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>A file that is a program, by its name or its first bytes ("MZ", ELF, a script's #!).</summary>
    public static bool IsExecutable(string name, ReadOnlySpan<byte> data) =>
        BlockedExt.Contains(Path.GetExtension(name))
        || (data.Length >= 2 && data[0] == 'M' && data[1] == 'Z')
        || (data.Length >= 4 && data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F')
        || (data.Length >= 2 && data[0] == '#' && data[1] == '!');

    public static string? ImageMime(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 ? "image/png"
        : d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? "image/jpeg"
        : d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' ? "image/gif"
        : d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P' ? "image/webp"
        : null;

    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { "thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db", "icon\r" };

    /// <summary>What a file is by its first bytes, whatever its name says: a renamed PDF is read as a PDF.</summary>
    private static string Sniff(string ext, byte[] d)
    {
        if (d.Length >= 5 && d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F') return ".pdf";
        if (d.Length >= 4 && d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4 && ext is not (".docx" or ".docm" or ".dotx" or ".pptx" or ".pptm" or ".xlsx" or ".xlsm" or ".odt" or ".ods" or ".epub" or ".vsdx" or ".zip" or ".xlsb"))
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(d));
                return zip.GetEntry("word/document.xml") is not null ? ".docx" : zip.GetEntry("ppt/presentation.xml") is not null ? ".pptx"
                    : zip.GetEntry("xl/workbook.xml") is not null ? ".xlsx" : zip.GetEntry("content.xml") is not null && zip.GetEntry("mimetype") is { } mt
                        && new StreamReader(mt.Open()).ReadToEnd().Contains("spreadsheet", StringComparison.Ordinal) ? ".ods"
                    : zip.GetEntry("content.xml") is not null ? ".odt" : ".zip";
            }
            catch (InvalidDataException) { return ext; }
        }
        if (d.Length >= 8 && d[0] == 0xD0 && d[1] == 0xCF && d[2] == 0x11 && d[3] == 0xE0 && ext is ".docx" or ".xlsx" or ".pptx" or ".txt") return ext == ".txt" ? ".doc" : "encrypted";
        return ext;
    }

    /// <summary>
    /// What a file holds, its text normalized for every reader: line ends as LF (CRLF and a lone CR alike), control characters
    /// other than tab and line end removed, so no check or model call sees them.
    /// </summary>
    public static Extraction Extract(string name, byte[] data)
    {
        var x = Read(name, data);
        return x.Text.Length == 0 ? x : x with { Text = Normalize(x.Text) };
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { sb.Append('\n'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c is '\n' or '\t' || (!char.IsControl(c) && c != '\uFEFF')) sb.Append(c);
        }
        return sb.ToString();
    }

    private static Extraction Read(string name, byte[] data)
    {
        var file = Path.GetFileName(name);
        if (Junk.Contains(file) || file.StartsWith("~$", StringComparison.Ordinal) || name.Contains("__MACOSX/", StringComparison.Ordinal) || file.StartsWith("._", StringComparison.Ordinal))
            return Extraction.Skipped("A system file: skipped.");
        if (data.Length == 0) return Extraction.Unsupported("The file is empty.");
        if (IsExecutable(name, data)) return Extraction.Blocked("A program or script: not imported.");
        if (ImageMime(data) is { } mime) return new Extraction(ExtractKind.Image, "", [new ExtractedImage(Path.GetFileName(name), data, mime)], []);
        var ext = Sniff(Path.GetExtension(name).ToLowerInvariant(), data);
        if (ext == "encrypted") return Extraction.Unsupported("Protected with a password: it cannot be read. Kept as an attachment.");
        try
        {
            return ext switch
            {
                ".txt" or ".text" or ".log" or ".md" or ".markdown" or ".ics" or ".vcf" or ".url" or "" => Extraction.Of(Decode(data)),
                ".sql" => Extraction.Of($"```sql\n{Decode(data).TrimEnd()}\n```"),
                ".json" or ".xml" or ".yaml" or ".yml" or ".ini" or ".conf" or ".cfg" or ".toml" => Extraction.Of($"```{ext.TrimStart('.')}\n{Decode(data).TrimEnd()}\n```"),
                ".csv" => Extraction.Of(Table(ReadDelimited(Decode(data), ','))),
                ".tsv" or ".tab" => Extraction.Of(Table(ReadDelimited(Decode(data), '\t'))),
                ".html" or ".htm" or ".xhtml" => Extraction.Of(HtmlToText(Decode(data))),
                ".mht" or ".mhtml" => Mht(data),
                ".eml" => Eml(data),
                ".docx" or ".docm" or ".dotx" => Docx(data),
                ".pptx" or ".pptm" => Pptx(data),
                ".xlsx" or ".xlsm" or ".xls" or ".xlsb" => Excel(data),
                ".epub" => Epub(data),
                ".vsdx" => Vsdx(data),
                ".msg" => Extraction.Of(BinaryStrings(data), note: "Outlook message: text read on a best effort basis; attachments are not."),
                ".ppt" => Extraction.Of(BinaryStrings(data), note: "Old PowerPoint file: text read on a best effort basis."),
                ".onetoc2" => Extraction.Skipped("A OneNote table of contents: no content of its own."),
                ".odt" => Odt(data),
                ".ods" => Ods(data),
                ".rtf" => Extraction.Of(Rtf(Encoding.GetEncoding(1252).GetString(data))),
                ".pdf" => Pdf(data),
                ".doc" => Extraction.Of(BinaryStrings(data), note: "Old Word file: text read on a best effort basis."),
                ".one" => Extraction.Of(BinaryStrings(data), note: "OneNote section: text read on a best effort basis; drawings and layout are not."),
                ".onepkg" => Extraction.Unsupported("OneNote package: export the sections as .one or PDF and import those."),
                ".zip" => Zip(data),
                _ => LooksLikeText(data) ? Extraction.Of(Decode(data)) : Extraction.Unsupported(NotRead),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (e is TooLargeException or RegexMatchTimeoutException)
                return Extraction.Unsupported((e is TooLargeException ? e.Message : "The file took too long to read.") + " It is kept as an attachment.");
            return Extraction.Unsupported(e is InvalidDataException or EndOfStreamException or FormatException || e.GetType().Name.EndsWith("FormatException", StringComparison.Ordinal)
                ? "The file is damaged or not what its name says, so it could not be read. It is kept as an attachment."
                : $"The file could not be read ({e.GetType().Name}). It is kept as an attachment.");
        }
    }

    // ---------- text and encodings

    /// <summary>Text in any encoding: a byte order mark, UTF-8 if it is valid, UTF-16 by its zero bytes, else Windows-1252.</summary>
    public static string Decode(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return Encoding.UTF8.GetString(d, 3, d.Length - 3);
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xFE && d[2] == 0 && d[3] == 0) return Encoding.UTF32.GetString(d, 4, d.Length - 4);
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
        var sample = Math.Min(d.Length, 4000);
        int zeroEven = 0, zeroOdd = 0;
        for (var i = 0; i < sample; i++) if (d[i] == 0) { if (i % 2 == 0) zeroEven++; else zeroOdd++; }
        if (zeroOdd > sample / 4 && zeroEven <= sample / 50) return Encoding.Unicode.GetString(d);
        if (zeroEven > sample / 4 && zeroOdd <= sample / 50) return Encoding.BigEndianUnicode.GetString(d);
        try { return new UTF8Encoding(false, true).GetString(d); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(d); }
    }

    private static bool LooksLikeText(byte[] d)
    {
        var n = Math.Min(d.Length, 4000);
        if (n == 0) return false;
        var odd = 0;
        for (var i = 0; i < n; i++) if (d[i] < 9 || (d[i] > 13 && d[i] < 32)) odd++;
        return odd < n / 100 + 1;
    }

    /// <summary>Readable runs of UTF-16 and 8-bit text in a binary file, in order, without duplicates of the same run.</summary>
    public static string BinaryStrings(byte[] d)
    {
        // Old Office files hold their text as UTF-16 or as single bytes (1252): both passes run. UTF-16 is read only in the
        // Latin, Greek, Cyrillic and punctuation ranges, so pairs of ASCII bytes are not taken for CJK letters.
        // shortcut: best effort for .doc/.ppt/.msg/.one without a real parser; CJK text in them is lost.
        var runs = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length >= 4 && s.Count(char.IsLetter) >= 2 && s.Count(char.IsLetterOrDigit) * 5 >= s.Length * 3) runs.Add(s);
            sb.Clear();
        }
        static bool Wide(char c) => c is (>= ' ' and < (char)0x7F) or (>= (char)0xA0 and <= (char)0x24F) or (>= (char)0x370 and <= (char)0x4FF) or (>= (char)0x2010 and <= (char)0x206F);
        for (var start = 0; start < 2; start++)
        {
            for (var i = start; i + 1 < d.Length; i += 2)
            {
                var c = (char)(d[i] | (d[i + 1] << 8));
                if (c is '\r' or '\n') Flush();
                else if (Wide(c)) sb.Append(c);
                else Flush();
            }
            Flush();
        }
        var latin = Encoding.GetEncoding(1252);
        for (var i = 0; i < d.Length; i++)
        {
            var b = d[i];
            if (b is >= 32 and < 127 or >= 0xC0) sb.Append(latin.GetString(d, i, 1));
            else Flush();
        }
        Flush();
        return string.Join("\n\n", runs.Distinct().Take(5000));
    }

    // ---------- tables

    private static List<List<string>> ReadDelimited(string text, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length && rows.Count < MaxRows; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>A Markdown table; the first row is the header.</summary>
    public static string Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return "";
        var width = rows.Max(r => r.Count);
        static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            sb.Append("| ").AppendJoin(" | ", Enumerable.Range(0, width).Select(c => c < rows[i].Count ? Cell(rows[i][c]) : "")).Append(" |\n");
            if (i == 0) sb.Append('|').AppendJoin("|", Enumerable.Repeat(" --- ", width)).Append("|\n");
        }
        return sb.ToString();
    }

    private static string Table(List<List<string>> rows) => Table(rows.Select(r => (IReadOnlyList<string>)r).ToList());

    // ---------- HTML, MHT, mail

    [GeneratedRegex(@"<(script|style|head|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternMs)]
    private static partial Regex Hidden();
    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternMs)]
    private static partial Regex HtmlHeading();
    [GeneratedRegex(@"<img\b[^>]*?src\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase, PatternMs)]
    private static partial Regex HtmlImg();
    [GeneratedRegex(@"<(/?)(ul|ol|li)\b[^>]*>", RegexOptions.IgnoreCase, PatternMs)]
    private static partial Regex HtmlList();
    [GeneratedRegex(@"</?(p|div|br|tr|table|section|article|blockquote|pre|hr)\b[^>]*>", RegexOptions.IgnoreCase, PatternMs)]
    private static partial Regex HtmlBreak();
    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase, PatternMs)]
    private static partial Regex HtmlCellEnd();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, PatternMs)]
    private static partial Regex Tag();
    [GeneratedRegex(@"[ \t]+", RegexOptions.None, PatternMs)]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}", RegexOptions.None, PatternMs)]
    private static partial Regex Blank();

    /// <summary>HTML as Markdown-like text: headings, list items, table cells split by "|", pictures named by their file.</summary>
    public static string HtmlToText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Length > MaxHtmlChars) throw new TooLargeException("The file holds more HTML than is read.");
        var s = Hidden().Replace(html, "");
        s = HtmlHeading().Replace(s, m => $"\n\n{new string('#', int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))} {Tag().Replace(m.Groups[2].Value, "").Trim()}\n\n");
        s = HtmlImg().Replace(s, m => $"\n![]({Path.GetFileName(Uri.UnescapeDataString(m.Groups[1].Value.Split('?')[0]))})\n");
        // A list item is a line with its mark, indented two spaces per level (\u0001 holds a level until the lines are trimmed).
        var lists = new Stack<bool>();
        s = HtmlList().Replace(s, m =>
        {
            var tag = m.Groups[2].Value.ToLowerInvariant();
            if (tag != "li") { if (m.Groups[1].Length == 0) lists.Push(tag == "ol"); else lists.TryPop(out _); return "\n"; }
            return m.Groups[1].Length > 0 ? "" : $"\n{new string('\u0001', Math.Max(lists.Count - 1, 0))}{(lists.TryPeek(out var ordered) && ordered ? "1. " : "- ")}";
        });
        s = HtmlCellEnd().Replace(s, " | ");
        s = HtmlBreak().Replace(s, "\n");
        s = WebUtility.HtmlDecode(Tag().Replace(s, ""));
        s = string.Join('\n', s.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()));
        return Blank().Replace(s, "\n\n").Trim().Replace("\u0001", "  ", StringComparison.Ordinal);
    }

    private static Extraction Mht(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var msg = MimeKit.MimeMessage.Load(ms);
        var images = new List<ExtractedImage>();
        string html = msg.HtmlBody ?? "", text = msg.TextBody ?? "";
        foreach (var part in msg.BodyParts.OfType<MimeKit.MimePart>())
        {
            if (!part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            using var buf = new MemoryStream();
            part.Content?.DecodeTo(buf);
            var bytes = buf.ToArray();
            var name = Path.GetFileName(part.ContentLocation?.ToString().Split('?')[0] ?? part.FileName ?? $"image{images.Count + 1}");
            if (ImageMime(bytes) is { } mime) images.Add(new ExtractedImage(name, bytes, mime));
        }
        return Extraction.Of(html.Length > 0 ? HtmlToText(html) : text, images);
    }

    private static Extraction Eml(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var msg = MimeKit.MimeMessage.Load(ms);
        var sb = new StringBuilder();
        sb.Append("# ").Append(msg.Subject ?? "(no subject)").Append("\n\n");
        sb.Append("From: ").Append(msg.From.ToString()).Append('\n');
        if (msg.Date != default) sb.Append("Date: ").Append(msg.Date.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n').Append(msg.TextBody is { Length: > 0 } t ? t : HtmlToText(msg.HtmlBody ?? ""));
        var children = new List<ExtractedFile>();
        foreach (var att in msg.Attachments.OfType<MimeKit.MimePart>())
        {
            using var buf = new MemoryStream();
            att.Content?.DecodeTo(buf);
            children.Add(new ExtractedFile(att.FileName ?? $"attachment{children.Count + 1}", buf.ToArray()));
        }
        return new Extraction(ExtractKind.Text, sb.ToString().Trim(), [], children);
    }

    // ---------- Office Open XML and OpenDocument

    private static XNamespace W => "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static XNamespace A => "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static XNamespace R => "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static XNamespace Rel => "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>A part that would unpack to more than a reader allows. The file is kept as an attachment and says so.</summary>
    private sealed class TooLargeException(string message) : Exception(message);

    private static XDocument? Xml(ZipArchive zip, string path) => zip.GetEntry(path) is { } e ? Xml(e) : null;

    private static XDocument Xml(ZipArchiveEntry e) => XDocument.Load(new MemoryStream(Bytes(e, MaxPartBytes)));

    /// <summary>An entry's bytes, counted while they are unpacked: the size an archive declares is not believed.</summary>
    private static byte[] Bytes(ZipArchiveEntry e, long max)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > max) throw new TooLargeException("A part inside the file is too large once unpacked.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>A part's relationships: id to the target's path inside the package.</summary>
    private static Dictionary<string, string> Rels(ZipArchive zip, string part)
    {
        var dir = Path.GetDirectoryName(part)!.Replace('\\', '/');
        var doc = Xml(zip, $"{dir}/_rels/{Path.GetFileName(part)}.rels");
        return doc?.Root?.Elements(Rel + "Relationship").ToDictionary(r => (string)r.Attribute("Id")!,
            r => Path.GetFullPath(Path.Combine("/" + dir, (string)r.Attribute("Target")!)).Replace('\\', '/').TrimStart('/').Split(':').Last().TrimStart('/')) ?? [];
    }

    private static List<ExtractedImage> Media(ZipArchive zip, string prefix) =>
        [.. zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Length <= MaxMediaBytes).Select(e => (e, Bytes(e, MaxMediaBytes)))
            .Where(x => ImageMime(x.Item2) is not null).Select(x => new ExtractedImage(Path.GetFileName(x.e.FullName), x.Item2, ImageMime(x.Item2)!))];

    private static Extraction Docx(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var doc = Xml(zip, "word/document.xml") ?? throw new InvalidDataException("No document.");
        var rels = Rels(zip, "word/document.xml");
        // Which levels of which lists are numbered: a list's number names its definition, which has a format per level.
        var numbering = Xml(zip, "word/numbering.xml")?.Root;
        var numbered = new HashSet<(string, string)>();
        foreach (var num in numbering?.Elements(W + "num") ?? [])
            foreach (var lvl in numbering!.Elements(W + "abstractNum").Where(a => (string?)a.Attribute(W + "abstractNumId") == (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val")).Elements(W + "lvl"))
                if ((string?)lvl.Element(W + "numFmt")?.Attribute(W + "val") is not (null or "bullet" or "none")) numbered.Add(((string?)num.Attribute(W + "numId") ?? "", (string?)lvl.Attribute(W + "ilvl") ?? "0"));
        var sb = new StringBuilder();
        var body = doc.Root!.Element(W + "body")!;
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p") sb.Append(Paragraph(block, rels, numbered)).Append('\n');
            else if (block.Name == W + "tbl")
            {
                var rows = block.Elements(W + "tr").Select(tr => (IReadOnlyList<string>)tr.Elements(W + "tc")
                    .Select(tc => string.Join(" ", tc.Descendants(W + "t").Select(t => t.Value))).ToList()).Take(MaxRows).ToList();
                sb.Append('\n').Append(Table(rows)).Append('\n');
            }
        }
        return Extraction.Of(Blank().Replace(sb.ToString(), "\n\n").Trim(), Media(zip, "word/media/"));
    }

    private static string Paragraph(XElement p, Dictionary<string, string> rels, HashSet<(string, string)> numbered)
    {
        var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
        var list = p.Element(W + "pPr")?.Element(W + "numPr");
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            if (node.Name == W + "t") sb.Append(node.Value);
            else if (node.Name == W + "tab") sb.Append(' ');
            else if (node.Name == W + "br") sb.Append('\n');
            else if (node.Name == A + "blip" && (string?)node.Attribute(R + "embed") is { } id && rels.TryGetValue(id, out var target))
                sb.Append(CultureInfo.InvariantCulture, $"\n![]({Path.GetFileName(target)})\n");
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0) return "";
        var heading = HeadingLevel().Match(style);
        if (heading.Success) return $"\n{new string('#', Math.Clamp(int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture), 1, 6))} {text}\n";
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return $"\n# {text}\n";
        if (list is null) return $"\n{text}\n";
        // A list item: its mark, indented two spaces per level. A level with no known number format reads as a bullet.
        var level = (string?)list.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
        var mark = (string?)list.Element(W + "numId")?.Attribute(W + "val") is { } num && numbered.Contains((num, level)) ? "1. " : "- ";
        return $"{new string(' ', 2 * (int.TryParse(level, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 8) : 0))}{mark}{text}";
    }

    [GeneratedRegex(@"^(?:Heading|heading)\s?([1-6])$")]
    private static partial Regex HeadingLevel();

    private static Extraction Pptx(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var slides = zip.Entries.Select(e => SlideNumber().Match(e.FullName)).Where(m => m.Success)
            .OrderBy(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Select(m => m.Value).ToList();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var path in slides)
        {
            n++;
            var doc = Xml(zip, path)!;
            var rels = Rels(zip, path);
            sb.Append(CultureInfo.InvariantCulture, $"\n## Slide {n}\n\n");
            foreach (var p in doc.Descendants(A + "p"))
            {
                var t = string.Concat(p.Descendants(A + "t").Select(x => x.Value)).Trim();
                if (t.Length > 0) sb.Append(t).Append('\n');
            }
            foreach (var blip in doc.Descendants(A + "blip"))
                if ((string?)blip.Attribute(R + "embed") is { } id && rels.TryGetValue(id, out var target)) sb.Append(CultureInfo.InvariantCulture, $"![]({Path.GetFileName(target)})\n");
        }
        return Extraction.Of(sb.ToString().Trim(), Media(zip, "ppt/media/"));
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlideNumber();

    private static Extraction Excel(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(ms);
        var sb = new StringBuilder();
        do
        {
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read() && rows.Count < MaxRows)
            {
                var row = new List<string>();
                for (var c = 0; c < reader.FieldCount; c++) row.Add(Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture) ?? "");
                while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                if (row.Count > 0) rows.Add(row);
            }
            if (rows.Count == 0) continue;
            sb.Append("## ").Append(reader.Name).Append("\n\n").Append(Table(rows)).Append('\n');
        } while (reader.NextResult());
        return Extraction.Of(sb.ToString().Trim());
    }

    private static XNamespace Text => "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static XNamespace TableNs => "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static XNamespace Draw => "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static XNamespace XLink => "http://www.w3.org/1999/xlink";

    private static Extraction Odt(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var doc = Xml(zip, "content.xml")!;
        var sb = new StringBuilder();
        var body = doc.Descendants(Text + "h").Concat(doc.Descendants(Text + "p")).Any() ? doc.Root! : throw new InvalidDataException("No text.");
        foreach (var el in body.Descendants().Where(e => e.Name == Text + "h" || (e.Name == Text + "p" && e.Ancestors(TableNs + "table").FirstOrDefault() is null) || e.Name == TableNs + "table"))
        {
            if (el.Name == TableNs + "table")
            {
                var rows = el.Descendants(TableNs + "table-row").Select(r => (IReadOnlyList<string>)r.Elements(TableNs + "table-cell")
                    .Select(c => string.Join(" ", c.Elements(Text + "p").Select(p => p.Value))).ToList()).Take(MaxRows).ToList();
                sb.Append('\n').Append(Table(rows)).Append('\n');
                continue;
            }
            var t = el.Value.Trim();
            foreach (var img in el.Descendants(Draw + "image")) sb.Append(CultureInfo.InvariantCulture, $"![]({Path.GetFileName((string?)img.Attribute(XLink + "href") ?? "")})\n");
            if (t.Length == 0) continue;
            if (el.Name == Text + "h") sb.Append(CultureInfo.InvariantCulture, $"\n{new string('#', Math.Clamp((int?)el.Attribute(Text + "outline-level") ?? 1, 1, 6))} {t}\n\n");
            else sb.Append(el.Ancestors(Text + "list-item").Count() is > 0 and var depth ? $"{new string(' ', 2 * (depth - 1))}- {t}\n" : $"{t}\n\n");
        }
        return Extraction.Of(sb.ToString().Trim(), Media(zip, "Pictures/"));
    }

    private static Extraction Ods(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var doc = Xml(zip, "content.xml")!;
        var sb = new StringBuilder();
        foreach (var table in doc.Descendants(TableNs + "table"))
        {
            var rows = new List<IReadOnlyList<string>>();
            foreach (var r in table.Elements(TableNs + "table-row").Take(MaxRows))
            {
                var row = new List<string>();
                foreach (var c in r.Elements(TableNs + "table-cell"))
                {
                    var repeat = Math.Min((int?)c.Attribute(TableNs + "number-columns-repeated") ?? 1, 50);
                    var v = string.Join(" ", c.Elements(Text + "p").Select(p => p.Value));
                    for (var i = 0; i < repeat; i++) row.Add(v);
                }
                while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                if (row.Count > 0) rows.Add(row);
            }
            if (rows.Count > 0) sb.Append("## ").Append((string?)table.Attribute(TableNs + "name") ?? "Sheet").Append("\n\n").Append(Table(rows)).Append('\n');
        }
        return Extraction.Of(sb.ToString().Trim());
    }

    private static Extraction Epub(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var pages = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal).Take(500);
        return Extraction.Of(string.Join("\n\n", pages.Select(e => HtmlToText(Decode(Bytes(e, MaxPartBytes))))), Media(zip, "OEBPS/images/"));
    }

    private static Extraction Vsdx(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        var sb = new StringBuilder();
        foreach (var e in zip.Entries.Where(e => PageXml().IsMatch(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n## {Path.GetFileNameWithoutExtension(e.FullName)}\n\n");
            foreach (var t in Xml(e).Descendants(v + "Text")) { var s = t.Value.Trim(); if (s.Length > 0) sb.Append("- ").Append(s).Append('\n'); }
        }
        return Extraction.Of(sb.ToString().Trim(), note: "A diagram: its shapes' text, without the drawing.");
    }

    [GeneratedRegex(@"^visio/pages/page\d+\.xml$")]
    private static partial Regex PageXml();

    // ---------- RTF

    /// <summary>RTF's text: paragraphs, tabs, \'hh and \uN characters; font tables, pictures and other destinations are skipped.</summary>
    public static string Rtf(string rtf)
    {
        var sb = new StringBuilder();
        var skip = new Stack<bool>();
        var skipping = false;
        var ansi = Encoding.GetEncoding(1252);
        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{') { skip.Push(skipping); continue; }
            if (c == '}') { skipping = skip.Count > 0 && skip.Pop(); continue; }
            if (c == '\\' && i + 1 < rtf.Length)
            {
                var n = rtf[i + 1];
                if (n is '\\' or '{' or '}') { if (!skipping) sb.Append(n); i++; continue; }
                if (n == '*') { skipping = true; i++; continue; }
                if (n == '\'' && i + 3 < rtf.Length)
                {
                    if (!skipping && byte.TryParse(rtf.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) sb.Append(ansi.GetString([b]));
                    i += 3; continue;
                }
                var j = i + 1;
                while (j < rtf.Length && char.IsAsciiLetter(rtf[j])) j++;
                var word = rtf[(i + 1)..j];
                var k = j;
                if (k < rtf.Length && (rtf[k] == '-' || char.IsAsciiDigit(rtf[k]))) { k++; while (k < rtf.Length && char.IsAsciiDigit(rtf[k])) k++; }
                var param = rtf[j..k];
                if (k < rtf.Length && rtf[k] == ' ') k++;
                i = k - 1;
                if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or "header" or "footer" or "listtable" or "listoverridetable" or "themedata" or "datastore" or "latentstyles")
                { skipping = true; continue; }
                if (skipping) continue;
                switch (word)
                {
                    case "par" or "line" or "row": sb.Append('\n'); break;
                    case "tab" or "cell": sb.Append(word == "cell" ? " | " : " "); break;
                    case "u" when int.TryParse(param, CultureInfo.InvariantCulture, out var u):
                        sb.Append((char)(u < 0 ? u + 65536 : u));
                        if (i + 1 < rtf.Length && rtf[i + 1] == '?') i++; // the plain-text stand-in that follows
                        break;
                }
                continue;
            }
            if (!skipping && c is not ('\r' or '\n')) sb.Append(c);
        }
        return Blank().Replace(sb.ToString(), "\n\n").Trim();
    }

    // ---------- PDF

    private static Extraction Pdf(byte[] data)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(data);
        var sb = new StringBuilder();
        var images = new List<ExtractedImage>();
        foreach (var page in pdf.GetPages())
        {
            var text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
            if (text.Trim().Length >= 20) { sb.Append(text.Trim()).Append("\n\n"); continue; }
            // A page with no text is a scan: its pictures go to the model's vision.
            foreach (var img in page.GetImages().Take(4))
                if (images.Count < 20 && img.TryGetPng(out var png)) images.Add(new ExtractedImage($"page{page.Number}-{images.Count + 1}.png", png, "image/png"));
            sb.Append(CultureInfo.InvariantCulture, $"![](page{page.Number}-{images.Count}.png)\n\n");
        }
        return Extraction.Of(sb.ToString().Trim(), images, images.Count > 0 ? "Scanned pages are read from their pictures." : null);
    }

    // ---------- zip

    private static Extraction Zip(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data));
        var entries = zip.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')).ToList();
        if (entries.Count > MaxZipEntries) return Extraction.Blocked($"More than {MaxZipEntries} files inside.");
        // Sizes as declared first: a bomb is refused before anything is unpacked.
        if (entries.Sum(e => e.Length) > MaxZipBytes) return Extraction.Blocked("Too large once unpacked.");
        return new Extraction(ExtractKind.Container, "", [], [.. entries.Select(e => new ExtractedFile(e.FullName, Bytes(e, e.Length)))]);
    }
}
