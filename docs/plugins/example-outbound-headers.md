# Example: an outbound-headers plugin

A plugin that adds two secret headers to the AI service's calls to one host. This is the shape of a plugin for an access gateway in front of a model server (Cloudflare Access, for example, wants a client id and a client secret in two headers).
This page builds a generic one. It was built, unit-tested, packed, installed and seen to add the headers, exactly as written here.

Related: [building and packaging](packaging.md), [developer guide](developer-guide.md#the-ai-service-and-outbound-headers).

## What the app gives

The AI service (Administration > AI) calls an OpenAI-compatible server: `POST {address}/v1/chat/completions`, and `GET {address}/v1/models` for the Model list.
Before each call it hands the request to every registered `IOutboundRequestDecorator`:

```csharp
public interface IOutboundRequestDecorator
{
    Task DecorateAsync(OutboundRequest request, CancellationToken ct);
}

public sealed record OutboundRequest(string Purpose, HttpRequestMessage Message);

public static class OutboundPurposes
{
    public const string Ai = "ai";   // the AI service's calls to its model server
}
```

- A decorator runs after the app set its own headers (`Authorization: Bearer <API key>` when a key is set), so it can add to them or replace them.
- It never sees the answer.
- Every decorator runs, in the order the plugins load.
- **Purposes.** `OutboundPurposes.Ai` is the only purpose today. A later version of the app may give decorators other calls (with another purpose). Check the purpose: act on the ones you were written for, so a new kind of call never gets your secrets by surprise.
- **Not configured, or misconfigured.** A plugin with no host set, or whose host does not match the request, does nothing: it returns and the call goes on unchanged. A plugin that is set up for this host but cannot add a header it needs (its secret is empty) throws. The call is then not sent, the person sees "A plugin that adds to the AI's requests failed (<exception type>).", and the AI's audit entry records the refusal. Never let a call to the gateway go out without the gateway's headers: the error then says what is wrong, where a gateway's refusal would not.
- The call goes through the app's own HTTP client, not the plugin's. `outboundHosts` does not apply; the plugin decides by the request's host.
- **Redirects.** The app follows no redirect on these calls. .NET keeps added headers when it follows a redirect to another host, so a redirect would carry your secret there. A model server that answers 3xx gets no second call; the person sees "The model server answered a redirect (302) to ...". Set the final address in Administration > AI.
- **Logs.** The app never logs these requests or their header values: its HTTP client logs header names with the values redacted, at every level. Do the same in your plugin: never log a secret, a header value, or an `HttpRequestMessage` (its `ToString()` prints every header and its value).

## The project

```
Example.GatewayHeaders/
├── Example.GatewayHeaders.csproj
├── kb-plugin.json
└── GatewayHeaders.cs
Example.GatewayHeaders.Tests/
├── Example.GatewayHeaders.Tests.csproj
└── GatewayHeadersTests.cs
lib/
└── Kb.Plugins.Abstractions.dll       built from the repository (see packaging)
```

`Example.GatewayHeaders.csproj` and `kb-plugin.json` are the ones in [building and packaging](packaging.md#the-project-file), word for word.
The settings:

| Key | Type | |
|---|---|---|
| `Host` | text | The host the headers go to, as in the AI's server address (`llm.example.com`). Calls to any other host get nothing. |
| `Header1Name`, `Header2Name` | text | The header names (`CF-Access-Client-Id`, `CF-Access-Client-Secret` for Cloudflare Access). |
| `Header1Value`, `Header2Value` | secret | Their values. Stored protected, never sent back to the browser, never logged. |

## The code

`GatewayHeaders.cs`:

```csharp
using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: KbPlugin(typeof(Example.GatewayHeaders.GatewayHeadersPlugin))]

namespace Example.GatewayHeaders;

public sealed class GatewayHeadersPlugin : IKbPlugin
{
    // A decorator is a service. The host gives every one the requests it makes for a purpose.
    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.AddSingleton<IOutboundRequestDecorator>(new GatewayHeaders(context));
}

/// <summary>Adds the two headers to the AI service's calls, and only to calls to the host in the settings.</summary>
public sealed class GatewayHeaders(IPluginContext context) : IOutboundRequestDecorator
{
    public async Task DecorateAsync(OutboundRequest request, CancellationToken ct)
    {
        if (request.Purpose != OutboundPurposes.Ai) return;
        var host = context.GetSetting("Host")?.Trim();
        if (string.IsNullOrEmpty(host) || !string.Equals(request.Message.RequestUri?.Host, host, StringComparison.OrdinalIgnoreCase)) return;
        await AddAsync(request.Message, "Header1Name", "Header1Value", ct);
        await AddAsync(request.Message, "Header2Name", "Header2Value", ct);
    }

    private async Task AddAsync(HttpRequestMessage message, string nameKey, string valueKey, CancellationToken ct)
    {
        var name = context.GetSetting(nameKey)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        // Secrets are read when they are needed: after start, never in ConfigureServices.
        var value = await context.GetSecretAsync(valueKey, ct)
            ?? throw new InvalidOperationException($"{valueKey} is not set."); // the host then refuses the call: it is never sent without it
        message.Headers.Remove(name);
        message.Headers.TryAddWithoutValidation(name, value);
    }
}
```

- `GetSetting` reads the current value each time: a change on the Plugins screen applies at once, without a restart.
- `GetSecretAsync` works only after the app has started. Do not call it in `ConfigureServices` or `Configure`.
- The plugin has no `Configure`: it adds no hooks, routes or jobs. `IKbPlugin.Configure` has a default that does nothing.

## The unit test

The test project references the plugin and the contract, and runs without the app. `Example.GatewayHeaders.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <Reference Include="Kb.Plugins.Abstractions"><HintPath>..\lib\Kb.Plugins.Abstractions.dll</HintPath></Reference>
    <ProjectReference Include="..\Example.GatewayHeaders\Example.GatewayHeaders.csproj" />
  </ItemGroup>
</Project>
```

`GatewayHeadersTests.cs`:

```csharp
using Kb.Plugins;
using Xunit;

namespace Example.GatewayHeaders.Tests;

public sealed class GatewayHeadersTests
{
    /// <summary>A plugin context with settings and secrets in memory, as an Admin would set them.</summary>
    private sealed class Context(Dictionary<string, string> settings, Dictionary<string, string> secrets) : IPluginContext
    {
        public PluginManifest Manifest => throw new NotSupportedException();
        public string? GetSetting(string key) => settings.GetValueOrDefault(key);
        public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => Task.FromResult(secrets.GetValueOrDefault(key));
    }

    private static GatewayHeaders Decorator(Dictionary<string, string>? secrets = null) => new(new Context(
        new() { ["Host"] = "llm.example.com", ["Header1Name"] = "X-Client-Id", ["Header2Name"] = "X-Client-Secret" },
        secrets ?? new() { ["Header1Value"] = "id-123", ["Header2Value"] = "secret-456" }));

    [Fact]
    public async Task AddsBothHeaders_ToTheAiCalls_ToTheHost()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "https://llm.example.com/v1/chat/completions");
        await Decorator().DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, message), CancellationToken.None);
        Assert.Equal("id-123", message.Headers.GetValues("X-Client-Id").Single());
        Assert.Equal("secret-456", message.Headers.GetValues("X-Client-Secret").Single());
    }

    [Fact]
    public async Task LeavesOtherHosts_AndOtherPurposes_Alone()
    {
        var other = new HttpRequestMessage(HttpMethod.Get, "https://elsewhere.example.com/v1/models");
        await Decorator().DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, other), CancellationToken.None);
        Assert.False(other.Headers.Contains("X-Client-Id"));
        var notAi = new HttpRequestMessage(HttpMethod.Get, "https://llm.example.com/v1/models");
        await Decorator().DecorateAsync(new OutboundRequest("something-else", notAi), CancellationToken.None);
        Assert.False(notAi.Headers.Contains("X-Client-Id"));
    }

    [Fact]
    public async Task AMissingSecret_Throws_SoTheCallIsNotSentWithoutIt()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "https://llm.example.com/v1/chat/completions");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Decorator(new()).DecorateAsync(new OutboundRequest(OutboundPurposes.Ai, message), CancellationToken.None));
    }
}
```

```bash
dotnet test Example.GatewayHeaders.Tests
```

## Pack and install

```powershell
dotnet publish Example.GatewayHeaders -c Release -o out
cd out
tar.exe -a -c -f ..\example.gateway-headers-1.0.0.zip *
cd ..
ren example.gateway-headers-1.0.0.zip example.gateway-headers-1.0.0.kbplugin
```

The package holds four files, all at the top: `kb-plugin.json`, `Example.GatewayHeaders.dll`, `.pdb` and `.deps.json`. The contract is not in it (`Private=false`).

1. Administration > Plugins > Upload, and choose the package. The app restarts.
2. Select the plugin. Set Host to the host of the AI's server address, the two header names, and the two values.
3. Administration > AI > Test connection. The test's call carries the headers.

The app's own test of a package does the same in a throwaway database, including the settings and the test connection, and checks the model server received both headers:

```bash
KB_PLUGIN_PACKAGE=example.gateway-headers-1.0.0.kbplugin dotnet test tests/Kb.Tests.Integration --filter "FullyQualifiedName~PackageFileTests"
```

## Before you ship one like it

- The secrets are only as safe as the app's settings. Admins can replace them, never read them.
- Match the host exactly. A pattern that also matches another host sends the secret there.
- Use `https` in the AI's server address. The headers travel with the request.
- When the gateway rotates its secret, set the new value on the Plugins screen; it applies to the next call.
