using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Kb.Core.Import;
using Kb.Tests.Integration.DataCenter;
using Kb.Web.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Kb.Tests.Integration.DataCenter.DcKit;

namespace Kb.Tests.Integration.Security;

/// <summary>
/// Findings M1 and M2 of the code review of 2026-11-17: what an import unpacks has a bound per part and per upload, and a pattern that runs
/// over a caller's text has a time limit. Each test proves the bound: an answer in bounded time, not a server that is still there by luck.
/// </summary>
public sealed class ImportBoundsTests
{
    private static byte[] Zip(params (string Path, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, data) in entries) { using var s = zip.CreateEntry(path, CompressionLevel.Fastest).Open(); s.Write(data); }
        return ms.ToArray();
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A small file whose one part unpacks to more than a reader allows is not read into memory: it is kept as an attachment.</summary>
    [Fact]
    public void OfficeFile_WithAPartOverTheLimit_IsNotRead()
    {
        const string open = """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>""", close = "</w:t></w:r></w:p></w:body></w:document>";
        var big = new byte[Extractors.MaxPartBytes + 1];
        Array.Fill(big, (byte)' ');
        U(open).CopyTo(big, 0);
        U(close).CopyTo(big, big.Length - close.Length);
        var docx = Zip(("word/document.xml", big));
        Assert.True(docx.Length < 1_048_576); // the upload itself is small
        var x = Extractors.Extract("a.docx", docx);
        Assert.Equal(ExtractKind.Unsupported, x.Kind);
        Assert.Contains("too large once unpacked", x.Note, StringComparison.Ordinal);

        var epub = Extractors.Extract("a.epub", Zip(("OEBPS/ch1.xhtml", big)));
        Assert.Equal(ExtractKind.Unsupported, epub.Kind);

        // Within the limit it is read as before.
        var small = Extractors.Extract("a.docx", Zip(("word/document.xml", U(open + "Inside Word" + close))));
        Assert.Contains("Inside Word", small.Text, StringComparison.Ordinal);
    }

    /// <summary>A picture over the limit inside a document is left out; the document's text is still read.</summary>
    [Fact]
    public void OfficeFile_PictureOverTheLimit_IsLeftOut()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[Extractors.MaxMediaBytes]];
        var epub = Extractors.Extract("a.epub", Zip(("OEBPS/ch1.xhtml", U("<html><body><p>Epub words</p></body></html>")), ("OEBPS/images/huge.png", png)));
        Assert.Contains("Epub words", epub.Text, StringComparison.Ordinal);
        Assert.Empty(epub.Images);
    }

    /// <summary>Two megabytes of a tag that is opened and never closed: each start position would rescan the rest of the text.</summary>
    [Fact]
    public void Html_ThatMakesAPatternRescan_AnswersInBoundedTime()
    {
        var html = U(string.Concat(Enumerable.Repeat("<script>", 250_000)));
        var clock = Stopwatch.StartNew();
        var x = Extractors.Extract("a.html", html);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
        Assert.True(x.Kind is ExtractKind.Unsupported or ExtractKind.Text);
        if (x.Kind == ExtractKind.Unsupported) Assert.Contains("too long to read", x.Note, StringComparison.Ordinal);

        Assert.Equal(ExtractKind.Unsupported, Extractors.Extract("b.html", U("<p>" + new string('x', Extractors.MaxHtmlChars) + "</p>")).Kind);
    }

    [Fact]
    public async Task Convert_EditorBodyThatMakesAPatternRescan_Is400InBoundedTime()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        await UserAsync(f, "cora");
        var clock = Stopwatch.StartNew();
        var r = await Basic(f, "cora").PostJsonAsync("/rest/api/contentbody/convert/storage", new { representation = "editor", value = "<p><br" + new string(' ', 2_000_000) });
        await r.ErrorAsync(HttpStatusCode.BadRequest);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
    }

    /// <summary>
    /// Archives inside one upload share one allowance. Three archives of 2000 files each are 6000 files: the first two are unpacked, the third
    /// stops at the allowance and says that files were left out.
    /// </summary>
    [Fact]
    public async Task Upload_UnpacksNoMoreThanItsAllowance()
    {
        await using var f = new KbAppFactory(KbAppFactory.NewDbPath());
        var user = await UserAsync(f, "cora");
        var archive = Zip([.. Enumerable.Range(0, Extractors.MaxZipEntries).Select(i => ($"f{i}.bin", new byte[] { 1 }))]);
        await using var scope = f.Services.CreateAsyncScope();
        var (db, imports) = (scope.ServiceProvider.GetRequiredService<Kb.Data.KbDbContext>(), scope.ServiceProvider.GetRequiredService<ImportService>());
        var batch = new ImportBatch { UserId = user, CreatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(Ct);
        foreach (var name in new[] { "one.zip", "two.zip", "three.zip" }) await imports.AddFileAsync(batch, name, archive, Ct);

        await imports.GroupBatchAsync(batch, Ct);

        var files = await db.ImportFiles.Where(x => x.BatchId == batch.Id).ToListAsync(Ct);
        Assert.Equal(3 + ImportService.MaxUnpackedFiles, files.Count);
        Assert.Equal(ImportService.OverBudget, files.Single(x => x.Name == "three.zip").Note);
        Assert.Null(files.Single(x => x.Name == "one.zip").Note);
    }
}
