using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = MigraDoc.DocumentObjectModel;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Kb.Web;

/// <summary>An image's bytes and pixel size, ready to place in a document.</summary>
public sealed record ExportImage(byte[] Data, string ContentType, int Width, int Height);

public static class Exports
{
    private const long MaxImageEmu = 5_943_600; // 6.5 in: the text width of a Letter page with 1 in margins
    private const double MaxImagePt = 468;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = "ps1", ["bash"] = "sh", ["shell"] = "sh", ["sql"] = "sql", ["json"] = "json", ["yaml"] = "yml", ["xml"] = "xml", ["ini"] = "ini",
        ["csharp"] = "cs", ["javascript"] = "js", ["typescript"] = "ts", ["python"] = "py", ["go"] = "go", ["java"] = "java", ["css"] = "css",
        ["markdown"] = "md", ["diff"] = "diff",
    };

    // PDFsharp finds fonts itself only when told to use the Windows font folder, and the setting can be made once.
    // Anywhere else (the Docker image) the Liberation fonts stand in for Arial and Courier New.
    static Exports()
    {
        if (OperatingSystem.IsWindows()) PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        else PdfSharp.Fonts.GlobalFontSettings.FontResolver = new LinuxFontResolver(LinuxFontResolver.SystemFolders);
    }

    public static string ExtensionFor(string? language) => Extensions.GetValueOrDefault(language ?? "", "txt");

    /// <summary>A safe file name from a title.</summary>
    public static string FileName(string? title, string fallback = "page")
    {
        var sb = new StringBuilder();
        foreach (var ch in (title ?? "").Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : char.IsWhiteSpace(ch) ? '-' : '\0');
        var name = sb.ToString().Replace("\0", "", StringComparison.Ordinal).Trim('-', '.');
        while (name.Contains("--", StringComparison.Ordinal)) name = name.Replace("--", "-", StringComparison.Ordinal);
        return name.Length == 0 ? fallback : name[..Math.Min(name.Length, 80)];
    }

    /// <summary>Pixel size from the file header. Null for formats Word and PDF can't hold (WebP).</summary>
    public static (int Width, int Height)? ImageSize(byte[] d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50)
            return ((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23]);
        if (d.Length >= 10 && d[0] == (byte)'G' && d[1] == (byte)'I')
            return (d[6] | (d[7] << 8), d[8] | (d[9] << 8));
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length && d[i] == 0xFF)
            {
                var marker = d[i + 1];
                var len = (d[i + 2] << 8) | d[i + 3];
                // Start-of-frame markers carry the size; C4, C8 and CC are other tables.
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }

    // ---- CSV and Excel ----

    public static byte[] Csv(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(cell =>
            {
                // A cell that starts like a formula would run in Excel; a leading apostrophe makes it text.
                var v = cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' && !double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "'" + cell : cell;
                return v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
            })).Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())]; // the BOM makes Excel read UTF-8
    }

    public static byte[] Xlsx(IReadOnlyList<IReadOnlyList<string>> rows, bool headerRow)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            var styles = wb.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font(), new S.Font(new S.Bold())),
                new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }), new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })),
                new S.Borders(new S.Border()),
                new S.CellFormats(new S.CellFormat(), new S.CellFormat { FontId = 1, ApplyFont = true }));
            var data = new S.SheetData();
            for (var r = 0; r < rows.Count; r++)
            {
                var row = new S.Row();
                foreach (var cell in rows[r])
                {
                    var c = double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && !double.IsInfinity(num) && cell.Length < 16
                        ? new S.Cell { DataType = S.CellValues.Number, CellValue = new S.CellValue(num) }
                        : new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(cell) { Space = SpaceProcessingModeValues.Preserve }) };
                    if (headerRow && r == 0) c.StyleIndex = 1;
                    row.Append(c);
                }
                data.Append(row);
            }
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new S.Worksheet(data);
            wb.Workbook = new S.Workbook(new S.Sheets(new S.Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Table" }));
        }
        return ms.ToArray();
    }

    // ---- Word ----

    public static byte[] Docx(string title, IReadOnlyList<DocBlock> blocks, IReadOnlyDictionary<Guid, ExportImage> images)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(images);
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(WordParagraph([new DocRun(title, Bold: true)], main, size: 40, after: 240));
            uint drawingId = 1;
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case DocParagraph p:
                        var size = p.Heading switch { 0 => 22, 1 => 34, 2 => 30, 3 => 26, _ => 24 };
                        var runs = p.Heading > 0 ? p.Runs.Select(r => r with { Bold = true }).ToList() : p.Runs;
                        if (p.Marker is not null) runs = [new DocRun(p.Marker + "\t"), .. runs];
                        body.Append(WordParagraph(runs, main, size, after: p.Heading > 0 ? 120 : p.Marker is null ? 160 : 60, before: p.Heading > 0 ? 240 : 0,
                            indent: p.Indent * 360, hanging: p.Marker is null ? 0 : 360, quote: p.Quote));
                        break;
                    case DocCode c:
                        foreach (var line in c.Text.Split('\n'))
                            body.Append(WordParagraph([new DocRun(line.TrimEnd('\r'), Code: true)], main, 19, after: 0, shade: true));
                        body.Append(new W.Paragraph());
                        break;
                    case DocTable t:
                        body.Append(WordTable(t), new W.Paragraph());
                        break;
                    case DocRule:
                        body.Append(new W.Paragraph(new W.ParagraphProperties(new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 6, Color = "BBBBBB" }))));
                        break;
                    case DocImage i when i.ImageId is { } id && images.TryGetValue(id, out var img):
                        body.Append(WordImage(main, img, i, drawingId++));
                        break;
                    case DocImage i:
                        body.Append(WordParagraph([new DocRun($"[image: {i.Alt}]", Italic: true)], main, 22, 160));
                        break;
                }
            }
            main.Document = new W.Document(body);
        }
        return ms.ToArray();
    }

    private static W.Paragraph WordParagraph(IReadOnlyList<DocRun> runs, MainDocumentPart main, int size, int after, int before = 0, int indent = 0, int hanging = 0, bool quote = false, bool shade = false)
    {
        var props = new W.ParagraphProperties();
        if (quote) props.Append(new W.ParagraphBorders(new W.LeftBorder { Val = W.BorderValues.Single, Size = 12, Space = 8, Color = "BBBBBB" }));
        if (shade) props.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F3F4F6" });
        props.Append(new W.SpacingBetweenLines { Before = before.ToString(CultureInfo.InvariantCulture), After = after.ToString(CultureInfo.InvariantCulture) });
        if (indent > 0) props.Append(new W.Indentation { Left = indent.ToString(CultureInfo.InvariantCulture), Hanging = hanging.ToString(CultureInfo.InvariantCulture) });
        var p = new W.Paragraph(props);
        foreach (var r in runs)
        {
            var rp = new W.RunProperties();
            if (r.Code) rp.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
            if (r.Bold) rp.Append(new W.Bold());
            if (r.Italic || quote) rp.Append(new W.Italic());
            if (r.Strike) rp.Append(new W.Strike());
            if (r.Href is not null) rp.Append(new W.Color { Val = "1D4ED8" });
            if (r.Underline || r.Href is not null) rp.Append(new W.Underline { Val = W.UnderlineValues.Single });
            rp.Append(new W.FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
            var run = new W.Run(rp);
            var parts = r.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) run.Append(new W.Break());
                var tabs = parts[i].Split('\t');
                for (var t = 0; t < tabs.Length; t++)
                {
                    if (t > 0) run.Append(new W.TabChar());
                    run.Append(new W.Text(tabs[t]) { Space = SpaceProcessingModeValues.Preserve });
                }
            }
            if (r.Href is not null && Uri.TryCreate(r.Href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                p.Append(new W.Hyperlink(run) { Id = main.AddHyperlinkRelationship(uri, true).Id });
            else p.Append(run);
        }
        return p;
    }

    private static W.Table WordTable(DocTable t)
    {
        static W.BorderType Line<T>() where T : W.BorderType, new() => new T { Val = W.BorderValues.Single, Size = 4, Color = "BBBBBB" };
        var table = new W.Table(new W.TableProperties(
            new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }, // full text width, so wide tables fit the page
            new W.TableBorders(Line<W.TopBorder>(), Line<W.LeftBorder>(), Line<W.BottomBorder>(), Line<W.RightBorder>(), Line<W.InsideHorizontalBorder>(), Line<W.InsideVerticalBorder>())));
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var row = new W.TableRow();
            foreach (var cell in t.Rows[r])
            {
                var rp = new W.RunProperties(new W.FontSize { Val = "20" });
                if (t.HeaderRow && r == 0) rp.PrependChild(new W.Bold());
                row.Append(new W.TableCell(new W.Paragraph(new W.Run(rp, new W.Text(cell) { Space = SpaceProcessingModeValues.Preserve }))));
            }
            table.Append(row);
        }
        return table;
    }

    private static W.Paragraph WordImage(MainDocumentPart main, ExportImage img, DocImage block, uint id)
    {
        var part = main.AddImagePart(img.ContentType switch { "image/jpeg" => ImagePartType.Jpeg, "image/gif" => ImagePartType.Gif, _ => ImagePartType.Png });
        using (var s = new MemoryStream(img.Data)) part.FeedData(s);
        var widthPx = block.Width is > 0 ? Math.Min(block.Width.Value, img.Width) : img.Width;
        var cx = Math.Min(widthPx * 9525L, MaxImageEmu);
        var cy = (long)(cx * (img.Height / (double)Math.Max(img.Width, 1)));
        var name = $"Image {id}";
        var drawing = new W.Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = name, Description = block.Alt },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        return new W.Paragraph(new W.Run(drawing));
    }

    // ---- PDF ----

    public static byte[] Pdf(string title, IReadOnlyList<DocBlock> blocks, IReadOnlyDictionary<Guid, ExportImage> images)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(images);
        var doc = new M.Document();
        doc.Info.Title = title;
        var normal = doc.Styles[M.StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10.5;
        var sec = doc.AddSection();
        sec.PageSetup.PageFormat = M.PageFormat.Letter;
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = M.Unit.FromInch(1);

        var head = sec.AddParagraph();
        head.Format.Font.Size = 20;
        head.Format.Font.Bold = true;
        head.Format.SpaceAfter = 12;
        head.AddText(title);

        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocParagraph p:
                    var para = sec.AddParagraph();
                    para.Format.SpaceAfter = p.Heading > 0 ? 6 : p.Marker is null ? 8 : 3;
                    para.Format.LeftIndent = M.Unit.FromPoint(p.Indent * 18);
                    if (p.Heading > 0) { para.Format.Font.Size = p.Heading switch { 1 => 17, 2 => 15, 3 => 13, _ => 12 }; para.Format.Font.Bold = true; para.Format.SpaceBefore = 12; para.Format.KeepWithNext = true; }
                    if (p.Quote) { para.Format.Font.Italic = true; para.Format.Font.Color = M.Colors.DimGray; }
                    if (p.Marker is not null)
                    {
                        para.Format.FirstLineIndent = M.Unit.FromPoint(-18);
                        para.Format.TabStops.AddTabStop(M.Unit.FromPoint(p.Indent * 18));
                        para.AddText(p.Marker switch { "☑" => "[x]", "☐" => "[ ]", var m => m });
                        para.AddTab();
                    }
                    foreach (var r in p.Runs) PdfRun(para, r);
                    break;
                case DocCode c:
                    var code = sec.AddParagraph();
                    code.Format.Font.Name = "Courier New";
                    code.Format.Font.Size = 9;
                    code.Format.Shading.Color = new M.Color(243, 244, 246);
                    code.Format.SpaceAfter = 8;
                    var lines = c.Text.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) code.AddLineBreak();
                        // Leading spaces collapse in PDF text; non-breaking ones keep the indentation.
                        var line = lines[i].TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);
                        var lead = line.Length - line.TrimStart(' ').Length;
                        code.AddText(new string(' ', lead) + line[lead..]);
                    }
                    break;
                case DocTable t when t.Rows.Count > 0:
                    var table = sec.AddTable();
                    table.Borders.Width = 0.5;
                    table.Borders.Color = M.Colors.Gray;
                    var cols = Math.Max(t.Rows.Max(r => r.Count), 1);
                    for (var i = 0; i < cols; i++) table.AddColumn(M.Unit.FromPoint(MaxImagePt / cols)); // equal columns across the text width
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        var row = table.AddRow();
                        for (var i = 0; i < t.Rows[r].Count; i++)
                        {
                            var cell = row.Cells[i].AddParagraph(t.Rows[r][i]);
                            cell.Format.Font.Size = 9.5;
                            cell.Format.Font.Bold = t.HeaderRow && r == 0;
                        }
                        if (t.HeaderRow && r == 0) { row.HeadingFormat = true; row.Shading.Color = new M.Color(243, 244, 246); }
                    }
                    sec.AddParagraph().Format.SpaceAfter = 4;
                    break;
                case DocRule:
                    var rule = sec.AddParagraph();
                    rule.Format.Borders.Bottom.Width = 0.5;
                    rule.Format.Borders.Bottom.Color = M.Colors.Gray;
                    rule.Format.SpaceAfter = 8;
                    break;
                case DocImage i when i.ImageId is { } id && images.TryGetValue(id, out var img) && img.ContentType != "image/gif":
                    var picture = sec.AddImage("base64:" + Convert.ToBase64String(img.Data));
                    picture.LockAspectRatio = true;
                    // Screen pixels are 96 per inch; points are 72.
                    picture.Width = M.Unit.FromPoint(Math.Min((i.Width is > 0 ? Math.Min(i.Width.Value, img.Width) : img.Width) * 0.75, MaxImagePt));
                    sec.AddParagraph().Format.SpaceAfter = 4;
                    break;
                case DocImage i:
                    var missing = sec.AddParagraph($"[image: {i.Alt}]");
                    missing.Format.Font.Italic = true;
                    break;
            }
        }

        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void PdfRun(M.Paragraph para, DocRun r)
    {
        var parts = r.Text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) para.AddLineBreak();
            if (parts[i].Length == 0) continue;
            var text = r.Href is not null && Uri.TryCreate(r.Href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
                ? para.AddHyperlink(r.Href, M.HyperlinkType.Web).AddFormattedText(parts[i])
                : para.AddFormattedText(parts[i]);
            if (r.Bold) text.Bold = true;
            if (r.Italic) text.Italic = true;
            if (r.Underline || r.Href is not null) text.Underline = M.Underline.Single;
            if (r.Href is not null) text.Color = new M.Color(29, 78, 216);
            if (r.Code) text.Font.Name = "Courier New";
            // shortcut: PDF has no strikethrough here (MigraDoc lacks it); the text still exports.
        }
    }
}
