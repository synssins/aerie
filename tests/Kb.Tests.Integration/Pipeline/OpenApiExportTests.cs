using Kb.Web.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Kb.Tests.Integration.Pipeline;

public sealed partial class RegistryTests
{
    /// <summary>
    /// docs/openapi/aerie-as-is.yaml describes /api/v1 as it is today, written from the running app's routes.
    /// Run this test with KB_WRITE_REFS=1 after adding or changing a route, a body or an action result.
    /// </summary>
    [Fact]
    public void OpenApiExport_IsUpToDate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kb.sln"))) dir = dir.Parent;
        var text = OpenApiExport.Write(Routes(), _f.Services.GetRequiredService<ActionCatalog>(), Path.Combine(dir!.FullName, "src", "Kb.Web"));
        var file = Path.Combine(dir.FullName, "docs", "openapi", "aerie-as-is.yaml");
        if (Environment.GetEnvironmentVariable("KB_WRITE_REFS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        Assert.True(File.Exists(file), "docs/openapi/aerie-as-is.yaml is missing. Run this test with KB_WRITE_REFS=1.");
        Assert.Equal(text, File.ReadAllText(file).ReplaceLineEndings("\n"));
    }
}
