using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Kb.Core.Import;
using Kb.Web.Import;

namespace Kb.Tests.Integration;

/// <summary>Reading every format an import takes, grouping files, building pages from the AI's blocks, and finding secrets.</summary>
public sealed class ImportExtractTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static byte[] Zip(params (string Path, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, data) in entries) { using var s = zip.CreateEntry(path).Open(); s.Write(data); }
        return ms.ToArray();
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Text_InAnyEncoding()
    {
        Assert.Equal("Café", Extractors.Extract("a.txt", [0xEF, 0xBB, 0xBF, .. U("Café")]).Text);
        Assert.Equal("Café", Extractors.Extract("a.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Café")]).Text);
        Assert.Equal("Café", Extractors.Extract("a.txt", Encoding.GetEncoding(1252).GetBytes("Café")).Text);
        Assert.Equal("Café restart", Extractors.Extract("a.log", Encoding.Unicode.GetBytes("Café restart")).Text);
        Assert.StartsWith("```json", Extractors.Extract("a.json", U("""{"a":1}""")).Text, StringComparison.Ordinal);
        Assert.Contains("| Name | Note |", Extractors.Extract("a.csv", U("Name,Note\nVPN,\"a, b\"\n")).Text, StringComparison.Ordinal);
        Assert.Contains("| VPN | a, b |", Extractors.Extract("a.csv", U("Name,Note\nVPN,\"a, b\"\n")).Text, StringComparison.Ordinal);
        Assert.Contains("| x | y |", Extractors.Extract("a.tsv", U("x\ty\n1\t2\n")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Programs_AreBlocked_ByNameOrBytes()
    {
        Assert.Equal(ExtractKind.Blocked, Extractors.Extract("setup.exe", [1, 2, 3]).Kind);
        Assert.Equal(ExtractKind.Blocked, Extractors.Extract("installer.msi", [1, 2, 3]).Kind);
        Assert.Equal(ExtractKind.Blocked, Extractors.Extract("fix.bat", U("@echo off")).Kind);
        Assert.Equal(ExtractKind.Blocked, Extractors.Extract("notes.txt", [(byte)'M', (byte)'Z', 0x90, 0]).Kind); // a program renamed
        Assert.Equal(ExtractKind.Image, Extractors.Extract("shot.png", Png).Kind);
    }

    [Fact]
    public void Html_Rtf_Mht_Eml()
    {
        var html = Extractors.Extract("a.html", U("<html><head><style>x{}</style></head><body><h1>Reset</h1><p>Open <b>Settings</b>.</p><ul><li>One</li><li>Two</li></ul><img src=\"img/step1.png\"><script>alert(1)</script></body></html>")).Text;
        Assert.Contains("# Reset", html, StringComparison.Ordinal);
        Assert.Contains("- One", html, StringComparison.Ordinal);
        Assert.Contains("![](step1.png)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", html, StringComparison.Ordinal);

        Assert.Equal("Hello café\nWorld", Extractors.Extract("a.rtf", U(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello caf\'e9\par World}")).Text);

        var mail = new MimeKit.MimeMessage { Subject = "VPN outage" };
        mail.From.Add(new MimeKit.MailboxAddress("Ops", "ops@example.com"));
        var builder = new MimeKit.BodyBuilder { TextBody = "Restart the concentrator." };
        builder.Attachments.Add("steps.txt", U("1. Log in"));
        builder.Attachments.Add("tool.exe", [(byte)'M', (byte)'Z']);
        mail.Body = builder.ToMessageBody();
        using var ms = new MemoryStream();
        mail.WriteTo(ms, TestContext.Current.CancellationToken);
        var eml = Extractors.Extract("outage.eml", ms.ToArray());
        Assert.Contains("# VPN outage", eml.Text, StringComparison.Ordinal);
        Assert.Contains("Restart the concentrator.", eml.Text, StringComparison.Ordinal);
        Assert.Equal(["steps.txt", "tool.exe"], eml.Children.Select(c => c.Path));

        var page = new MimeKit.MimeMessage { Subject = "page" };
        var related = new MimeKit.MultipartRelated { new MimeKit.TextPart("html") { Text = "<h2>Saved page</h2><img src=\"https://x/pic.png\">" } };
        related.Add(new MimeKit.MimePart("image", "png") { Content = new MimeKit.MimeContent(new MemoryStream(Png)), ContentLocation = new Uri("https://x/pic.png") });
        page.Body = related;
        using var ms2 = new MemoryStream();
        page.WriteTo(ms2, TestContext.Current.CancellationToken);
        var mht = Extractors.Extract("saved.mht", ms2.ToArray());
        Assert.Contains("## Saved page", mht.Text, StringComparison.Ordinal);
        Assert.Equal("pic.png", Assert.Single(mht.Images).Name);
    }

    [Fact]
    public void Docx_Pptx_Odt_Ods_Xlsx()
    {
        const string Wns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var docx = Zip(
            ("word/document.xml", U($"<w:document {Wns}><w:body>" +
                "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Reset MFA</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Open the portal</w:t></w:r></w:p>" +
                "<w:p><w:r><w:drawing><a:blip r:embed=\"rId5\"/></w:drawing></w:r></w:p>" +
                "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Key</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Value</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                "</w:body></w:document>")),
            ("word/_rels/document.xml.rels", U("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId5\" Target=\"media/image1.png\"/></Relationships>")),
            ("word/media/image1.png", Png));
        var d = Extractors.Extract("guide.docx", docx);
        Assert.Contains("# Reset MFA", d.Text, StringComparison.Ordinal);
        Assert.Contains("- Open the portal", d.Text, StringComparison.Ordinal);
        Assert.Contains("![](image1.png)", d.Text, StringComparison.Ordinal);
        Assert.Contains("| Key | Value |", d.Text, StringComparison.Ordinal);
        Assert.Equal("image1.png", Assert.Single(d.Images).Name);

        var pptx = Zip(("ppt/slides/slide2.xml", U("<p:sld xmlns:p=\"x\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>")),
            ("ppt/slides/slide1.xml", U("<p:sld xmlns:p=\"x\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:p><a:r><a:t>First</a:t></a:r></a:p></p:sld>")));
        var p = Extractors.Extract("deck.pptx", pptx).Text;
        Assert.True(p.IndexOf("First", StringComparison.Ordinal) < p.IndexOf("Second", StringComparison.Ordinal));

        const string Ons = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"";
        var odt = Extractors.Extract("a.odt", Zip(("content.xml", U($"<office:document-content {Ons}><office:body><office:text><text:h text:outline-level=\"2\">Patch</text:h><text:p>Run the update.</text:p></office:text></office:body></office:document-content>")))).Text;
        Assert.Contains("## Patch", odt, StringComparison.Ordinal);
        Assert.Contains("Run the update.", odt, StringComparison.Ordinal);
        var ods = Extractors.Extract("a.ods", Zip(("content.xml", U($"<office:document-content {Ons}><office:body><office:spreadsheet><table:table table:name=\"Servers\"><table:table-row><table:table-cell><text:p>Host</text:p></table:table-cell><table:table-cell><text:p>IP</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>")))).Text;
        Assert.Contains("## Servers", ods, StringComparison.Ordinal);
        Assert.Contains("| Host | IP |", ods, StringComparison.Ordinal);

        var xlsx = Zip(
            ("[Content_Types].xml", U("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>")),
            ("_rels/.rels", U("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")),
            ("xl/workbook.xml", U("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Apps\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", U("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>")),
            ("xl/worksheets/sheet1.xml", U("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>App</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Version</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>ERP</t></is></c><c r=\"B2\"><v>4</v></c></row></sheetData></worksheet>")));
        var x = Extractors.Extract("apps.xlsx", xlsx).Text;
        Assert.Contains("## Apps", x, StringComparison.Ordinal);
        Assert.Contains("| ERP | 4 |", x, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_AndZipsWithinZips_AndOneNotePackages()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Rotate the backup tapes every Friday.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        Assert.Contains("Rotate the backup tapes", Extractors.Extract("tapes.pdf", builder.Build()).Text, StringComparison.Ordinal);

        var inner = Zip(("deep/notes.txt", U("inner text")));
        var outer = Extractors.Extract("bundle.zip", Zip(("readme.md", U("# Readme")), ("more.zip", inner)));
        Assert.Equal(ExtractKind.Container, outer.Kind);
        Assert.Equal(["readme.md", "more.zip"], outer.Children.Select(c => c.Path));
        Assert.Equal(ExtractKind.Container, Extractors.Extract("more.zip", outer.Children[1].Data).Kind);

        Assert.Equal(ExtractKind.Unsupported, Extractors.Extract("book.onepkg", [0x4D, 0x53, 0x43, 0x46]).Kind);
        Assert.Contains("Escalate to the ERP team", Extractors.Extract("section.one", [.. new byte[64], .. Encoding.Unicode.GetBytes("Escalate to the ERP team"), .. new byte[8]]).Text, StringComparison.Ordinal);
    }

    private static ImportFile F(string name, string kind, string? text = null, Guid? parent = null, bool embedded = false) =>
        new() { Path = name, Name = name, Sha256 = "x", Kind = kind, Text = text, ParentId = parent, Embedded = embedded };

    [Fact]
    public void Grouping_PicturesGoWithTheDocumentThatNamesThem_AndMailWithItsAttachments()
    {
        var guide = F("vpn-setup.md", "Text", "Step 1 ![](connect.png)");
        var named = F("connect.png", "Image");
        var stem = F("vpn-setup-2.png", "Image");
        var lone = F("holiday.jpg", "Image");
        var other = F("printers.txt", "Text", "Printers");
        var mail = F("outage.eml", "Text", "VPN outage");
        var att = F("log.txt", "Text", "log", mail.Id);
        var zip = F("bundle.zip", "Container");
        var inner = F("img.png", "Image", null, guide.Id, embedded: true);
        var groups = ImportService.Group([guide, named, stem, lone, other, mail, att, zip, inner]);
        HashSet<Guid> Of(ImportFile f) => groups.Single(g => g.Contains(f.Id));
        Assert.Equal(new HashSet<Guid> { guide.Id, named.Id, stem.Id }, Of(guide));
        Assert.Equal(new HashSet<Guid> { lone.Id }, Of(lone));
        Assert.Equal(new HashSet<Guid> { other.Id }, Of(other));
        Assert.Contains(mail.Id, Of(att));
        Assert.DoesNotContain(groups, g => g.Contains(zip.Id) || g.Contains(inner.Id));
    }

    [Fact]
    public void Grouping_JoinsDocumentsThatNameTheSameChangeOrCase_AndSkipsSystemFiles()
    {
        var plan = F("plan.docx", "Text", "Change CHG0041234 upgrades the SAN.");
        var mail = F("cab.eml", "Text", "Approved: CHG0041234");
        var vendor = F("case.txt", "Text", "Vendor case LE-561164 opened for CHG0041234's failure.");
        var later = F("followup.txt", "Text", "Update on LE-561164.");
        var alone = F("printers.txt", "Text", "Printers on floor 2, ERP01.");
        var also = F("erp.txt", "Text", "ERP01 restart.");
        var junk = F("Thumbs.db", "Skipped");
        var groups = ImportService.ByIdentifiers(ImportService.Group([plan, mail, vendor, later, alone, also, junk]), [plan, mail, vendor, later, alone, also, junk]);
        Assert.Contains(groups, g => g.SetEquals(new[] { plan.Id, mail.Id, vendor.Id, later.Id }));
        Assert.Contains(groups, g => g.SetEquals(new[] { alone.Id })); // a server name is not a story
        var digest = F("calendar.pdf", "Text", string.Join(' ', Enumerable.Range(41230, 9).Select(n => $"CHG00{n}")) + " CHG0041234");
        var withDigest = ImportService.ByIdentifiers(ImportService.Group([plan, digest]), [plan, digest]);
        Assert.Equal(2, withDigest.Count); // a change calendar names many stories and joins none
        Assert.DoesNotContain(groups, g => g.Contains(junk.Id));

        // A folder with one document: its screenshots go with it. A folder with several: they don't guess.
        var howto = F("b1/howto.md", "Text", "Do the steps.");
        var shot = F("b1/step1.png", "Image");
        var a = F("b2/a.md", "Text", "A");
        var b = F("b2/b.md", "Text", "B");
        var loose = F("b2/shot.png", "Image");
        var folders = ImportService.Group([howto, shot, a, b, loose]);
        Assert.Contains(folders, g => g.SetEquals(new[] { howto.Id, shot.Id }));
        Assert.Contains(folders, g => g.SetEquals(new[] { loose.Id }));
    }

    [Fact]
    public void Text_IsNormalized_Compacted_AndTitlesCleaned()
    {
        // CRLF, LF and a lone CR alike; control characters (NUL, BEL) gone; tabs kept.
        Assert.Equal("a\nb\nc\nd\te", Extractors.Extract("x.txt", U("a\r\nb\nc\rd\te\0\a")).Text);
        Assert.Equal("one\ntwo", Extractors.Extract("cr.txt", U("one\rtwo")).Text);
        Assert.Equal("Start\nretry\n(the line above 2 more times)\n\nEnd", ImportService.Compact("Start\nretry\nretry\nretry\n\n\n\nEnd"));
        Assert.Equal("runbook final v2", ImportService.CleanTitle("runbook_final_v2.docx"));
        var longName = new string('a', 40) + " very long file name " + new string('b', 150) + ".txt";
        var t = ImportService.CleanTitle(longName);
        Assert.True(t.Length <= 101, t);
        Assert.StartsWith("aaa very long file name bbb", t, StringComparison.Ordinal);
        Assert.Equal("Restart A/B", ImportService.CleanTitle("Restart A/B", fileName: false));
    }

    [Fact]
    public void Found_KeepsOnlyWhatTheSourceSays_AndAddsTheNumbersItFinds()
    {
        var found = JsonNode.Parse("""{"people":["Lena Kowalski","Made Up"],"servers":["APP02"],"versions":["4.0.2","9.9"],"conflicts":["4.0.1 and 4.0.2 are both named for the upgrade."]}""")!.AsObject();
        var f = ImportService.Found(found, "Lena Kowalski upgrades APP02 to 4.0.2 under CHG0095447.");
        Assert.Equal(["Lena Kowalski"], f["people"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["4.0.2"], f["versions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["CHG0095447"], f["tickets"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Single(f["conflicts"]!.AsArray());
    }

    [Fact]
    public void Misnamed_Junk_Empty_AndMoreFormats()
    {
        // Old Office: single-byte and UTF-16 text both read, numbers kept, ASCII pairs not taken for CJK.
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, .. U("Change CHG0095447 approved"), 0, 1, 2, .. Encoding.Unicode.GetBytes("Rollback ready"), 0, 0];
        var old = Extractors.Extract("rfc.doc", ole).Text;
        Assert.Contains("Change CHG0095447 approved", old, StringComparison.Ordinal);
        Assert.Contains("Rollback ready", old, StringComparison.Ordinal);
        Assert.DoesNotContain(old, c => c is >= '　' and <= '鿿');
        Assert.Equal(ExtractKind.Skipped, Extractors.Extract("x/Thumbs.db", [1, 2]).Kind);
        Assert.Equal(ExtractKind.Skipped, Extractors.Extract("~$report.docx", [1, 2]).Kind);
        Assert.Equal(ExtractKind.Skipped, Extractors.Extract("__MACOSX/._a.txt", [1, 2]).Kind);
        Assert.Equal(ExtractKind.Unsupported, Extractors.Extract("empty.txt", []).Kind);
        // a Word file named .txt is read as Word
        var docx = Zip(("word/document.xml", U("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Inside Word</w:t></w:r></w:p></w:body></w:document>""")));
        Assert.Contains("Inside Word", Extractors.Extract("notes.txt", docx).Text, StringComparison.Ordinal);
        Assert.StartsWith("```sql", Extractors.Extract("q.sql", U("SELECT 1;")).Text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Patch night", Extractors.Extract("a.ics", U("BEGIN:VEVENT\nSUMMARY:Patch night\nEND:VEVENT")).Text, StringComparison.Ordinal);
        var epub = Zip(("OEBPS/ch1.xhtml", U("<html><body><h1>Chapter</h1><p>Epub words</p></body></html>")));
        Assert.Contains("Epub words", Extractors.Extract("book.epub", epub).Text, StringComparison.Ordinal);
        var vsdx = Zip(("visio/pages/page1.xml", U("""<PageContents xmlns="http://schemas.microsoft.com/office/visio/2012/main"><Shapes><Shape><Text>Firewall</Text></Shape></Shapes></PageContents>""")));
        Assert.Contains("- Firewall", Extractors.Extract("net.vsdx", vsdx).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_AreBuiltFromBlocks_WithMarksPicturesAndTables()
    {
        var blocks = DocBuilder.ReadBlocks(JsonNode.Parse("""
            [{"type":"heading","text":"Reset MFA","level":2},{"type":"steps","items":["Open **Settings**","Run `reset`"]},
             {"type":"image","image":"shot.png","text":"The settings page"},{"type":"image","image":"missing.png"},
             {"type":"table","rows":[["Key","Value"],["a","b"]]},{"type":"code","text":"Get-Mfa","language":"PowerShell"},{"type":"note","text":"Careful"}]
            """)!.AsArray());
        var doc = DocBuilder.Build(blocks, n => n == "shot.png" ? "/api/v1/imports/files/x" : null);
        var types = doc["content"]!.AsArray().Select(n => n!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["heading", "orderedList", "image", "table", "codeBlock", "blockquote"], types);
        Assert.Contains("\"bold\"", doc.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"code\"", doc.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("powershell", doc["content"]![4]!["attrs"]!["language"]!.GetValue<string>());
    }

    [Fact]
    public void Secrets_AreFound_AndRedacted()
    {
        const string Text = "Admin password: Hunter2Secret!\nkey AKIAIOSFODNN7EXAMPLE\nServer=db;User Id=sa;Password=P@ss123;\ncard 4111 1111 1111 1111\nSSN 123-45-6789\nplain words";
        var found = Sensitive.Find(Text);
        Assert.Contains(found, f => f.Value == "Hunter2Secret!");
        Assert.Contains(found, f => f.Value == "AKIAIOSFODNN7EXAMPLE");
        Assert.Contains(found, f => f.Value == "P@ss123");
        Assert.Contains(found, f => f.Kind == "Card number");
        Assert.Contains(found, f => f.Kind == "Social security number");
        Assert.All(found, f => Assert.DoesNotContain(f.Value, f.Sample, StringComparison.Ordinal));
        // A credentials list without labels; an account name before an ordinary word is not a password.
        var list = Sensitive.Find("APP01\tadministrator\tchangeme-NOT-REAL-123\nWEB01 | admin | P@ssw0rd\nOpen the admin console\nsk_test_abcdefghij12");
        Assert.Equal(["P@ssw0rd", "changeme-NOT-REAL-123", "sk_test_abcdefghij12"], list.Select(f => f.Value).Order(StringComparer.Ordinal));
        var doc = DocBuilder.Build([new AiBlock("paragraph", Text, 0, [], [], "", "")], _ => null);
        var redacted = Sensitive.Redact(doc, found).ToJsonString();
        Assert.DoesNotContain("Hunter2Secret!", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", redacted, StringComparison.Ordinal);
        Assert.Contains("plain words", redacted, StringComparison.Ordinal);
        Assert.Empty(Sensitive.Find("Restart the print spooler. The password policy requires 15 characters."));
    }

    /// <summary>A list as text, one line per item: its mark, indented two spaces per level; a line break in an item as " / ".</summary>
    private static string Outline(JsonNode list, int depth = 0) => string.Concat(list["content"]!.AsArray().Select(item =>
        new string(' ', depth * 2) + (list["type"]!.GetValue<string>() == "orderedList" ? "1. " : "- ") +
        string.Concat((item!["content"]![0]!["content"] as JsonArray ?? []).Select(n => n!["type"]!.GetValue<string>() == "hardBreak" ? " / " : n["text"]!.GetValue<string>())) + "\n" +
        string.Concat(item["content"]!.AsArray().Skip(1).Select(sub => Outline(sub!, depth + 1)))));

    private static string BuiltList(string type, params string[] items) =>
        Outline(Assert.Single(DocBuilder.Build([new AiBlock(type, "", 0, items, [], "", "")], _ => null)["content"]!.AsArray())!);

    [Fact]
    public void ListItems_WithIndentedMarkedLines_BecomeNestedLists()
    {
        // The stored page of the first report: sub-bullets came back inside the item's text.
        Assert.Equal(
            "1. Is the front light amber or blue?\n" +
            "  - blue and no POST -> go to \"the display\" below\n" +
            "  - amber blinking -> read the LCD panel. ...\n" +
            "  - no light at all -> is it plugged in? ...\n" +
            "1. Still nothing?\n",
            BuiltList("steps",
                "Is the front light amber or blue?\n   - blue and no POST -> go to \"the display\" below\n   - amber blinking -> read the LCD panel. ...\n   - no light at all -> is it plugged in? ...",
                "Still nothing?"));

        // Numbered under bullets; "*" and "1)" marks; no indentation at all.
        Assert.Equal("- Tools\n  1. Screwdriver\n  1. Torch\n- Parts\n  - DIMM\n  - PSU\n",
            BuiltList("bullets", "Tools\n  1. Screwdriver\n  2) Torch", "Parts\n* DIMM\n* PSU"));

        // Three levels, with lines that carry on an item's text at each level, and a step back out.
        Assert.Equal(
            "- NO -> the couch / It used to be a cow.\n" +
            "  - Is the couch available?\n" +
            "    - yes -> carry it to the door / Leave it facing rack 3.\n" +
            "    - no -> a belt\n" +
            "  - Say the service tag.\n",
            BuiltList("bullets",
                "NO -> the couch\n  It used to be a cow.\n  - Is the couch available?\n    - yes -> carry it to the door\n      Leave it facing rack 3.\n    - no -> a belt\n  - Say the service tag."));

        // Marks inside a nested item still apply; an item of several plain lines is as before.
        Assert.Contains("\"bold\"", DocBuilder.Build([new AiBlock("steps", "", 0, ["Open\n  - **Settings**"], [], "", "")], _ => null).ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("1. One / two\n", BuiltList("steps", "One\ntwo"));
    }

    [Fact]
    public void NestedLists_KeepTheirDepth_InTheTextTheModelReads()
    {
        const string Md = "1. Check the light\n   - blue\n     - deep\n2. Log in\n\n- Tools\n\t1. Screwdriver";
        Assert.Equal(Md, Extractors.Extract("a.md", U(Md)).Text);
        Assert.Equal(Md, Extractors.Extract("a.txt", U(Md.Replace("\n", "\r\n", StringComparison.Ordinal))).Text);
        Assert.Equal(Md, ImportService.Compact(Md).TrimEnd());

        const string Lists = "1. Check the light\n  - blue\n    - deep\n  - amber\n1. Log in\n- Tools\n  1. Screwdriver";
        var html = Extractors.Extract("a.html", U("<body><ol><li>Check the light<ul><li>blue<ul><li>deep</li></ul></li><li>amber</li></ul></li>\n <li>Log in</li></ol>" +
            "<ul><li>Tools<ol><li>Screwdriver</li></ol></li></ul></body>")).Text;
        Assert.Equal(Lists, string.Join('\n', html.Split('\n').Where(l => l.Length > 0)));

        const string Wns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        static string P(int num, int level, string text) => $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{num}\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";
        var docx = Extractors.Extract("a.docx", Zip(
            ("word/document.xml", U($"<w:document {Wns}><w:body>{P(1, 0, "Check the light")}{P(1, 1, "blue")}{P(1, 2, "deep")}{P(1, 1, "amber")}{P(1, 0, "Log in")}{P(2, 0, "Tools")}{P(2, 1, "Screwdriver")}</w:body></w:document>")),
            ("word/numbering.xml", U($"<w:numbering {Wns}>" +
                "<w:abstractNum w:abstractNumId=\"7\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"bullet\"/></w:lvl><w:lvl w:ilvl=\"2\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum>" +
                "<w:abstractNum w:abstractNumId=\"8\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"lowerLetter\"/></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"7\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"8\"/></w:num></w:numbering>")))).Text;
        Assert.Equal(Lists, docx);

        const string Ons = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var odt = Extractors.Extract("a.odt", Zip(("content.xml", U($"<office:document-content {Ons}><office:body><office:text><text:list><text:list-item><text:p>Check the light</text:p>" +
            "<text:list><text:list-item><text:p>blue</text:p><text:list><text:list-item><text:p>deep</text:p></text:list-item></text:list></text:list-item></text:list>" +
            "</text:list-item></text:list></office:text></office:body></office:document-content>")))).Text;
        Assert.Equal("- Check the light\n  - blue\n    - deep", odt);
    }
}
