using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kb.Core.Content;
using Kb.Core.Import;
using Kb.Tests.Integration.DataCenter;
using Kb.Web.Import;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Three places that called themselves once per level of data a caller builds, with no bound, found while fixing H2 of the code review
/// of 2026-11-17: the pages below a folder on the compatible API, the grouping of imported files by the numbers they share, and the text
/// of a deeply nested part of an office file. A stack overflow ends the process; each test passes because the process is still there.
/// </summary>
public sealed class RecursionBoundsTests
{
    [Fact]
    public async Task Descendants_OfAVeryDeepTree_AreListed()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var admin = await f.SignedInBreakGlassAsync();
        var space = (await (await admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Operations", key = "ops" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var top = (await (await admin.PostAsJsonAsync($"/api/v1/spaces/{space}/pages", new { kind = "Folder", title = "Top" }, Ct)).JsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        const int depth = 30_000;
        await f.WithDbAsync(async db =>
        {
            var me = db.Users.Single(u => u.IsBreakGlass).Id;
            var parent = top;
            for (var i = 0; i < depth; i++)
            {
                var page = new Page { SpaceId = space, ParentId = parent, Kind = PageKind.Folder, Title = "Level " + i, Sort = 1, CreatedBy = me, CreatedAt = DateTimeOffset.UtcNow, UpdatedBy = me, UpdatedAt = DateTimeOffset.UtcNow };
                db.Pages.Add(page);
                parent = page.Id;
            }
            await db.SaveChangesAsync(Ct);
        });
        await using var scope = f.Services.CreateAsyncScope();
        var content = scope.ServiceProvider.GetRequiredService<Kb.Web.DataCenter.DcContentService>();
        var db2 = scope.ServiceProvider.GetRequiredService<Kb.Data.KbDbContext>();
        var below = await content.DescendantsAsync(db2.Pages.Single(p => p.Id == top), Ct);
        Assert.Equal(depth, below.Count);
        Assert.Equal(["Level 0", "Level 1", "Level 2"], below.Take(3).Select(p => p.Title));
    }

    [Fact]
    public void ImportGrouping_OfALongChainOfFilesThatShareNumbers_IsOneGroup()
    {
        const int n = 200_000;
        var files = Enumerable.Range(0, n).Select(i => new ImportFile { Path = $"f{i}.txt", Name = $"f{i}.txt", Sha256 = "", Kind = "Text", Text = $"See CHG{100000 + i} and CHG{100001 + i}." }).ToList();
        var groups = ImportService.ByIdentifiers([.. files.Select(x => new HashSet<Guid> { x.Id })], files);
        Assert.Single(groups);
        Assert.Equal(n, groups[0].Count);
    }

    [Fact]
    public void OfficeFile_WithAPartNestedWithoutEnd_IsNotRead()
    {
        byte[] Docx(int depth)
        {
            var xml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + string.Concat(Enumerable.Repeat("<w:x>", depth)) + "deep"
                + string.Concat(Enumerable.Repeat("</w:x>", depth)) + "</w:t></w:r></w:p></w:body></w:document>";
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using var s = zip.CreateEntry("word/document.xml").Open(); s.Write(Encoding.UTF8.GetBytes(xml)); }
            return ms.ToArray();
        }
        var x = Extractors.Extract("a.docx", Docx(100_000));
        Assert.Equal(ExtractKind.Unsupported, x.Kind);
        Assert.Contains("nested too deep", x.Note, StringComparison.Ordinal);
        Assert.Contains("deep", Extractors.Extract("b.docx", Docx(50)).Text, StringComparison.Ordinal);
    }
}
