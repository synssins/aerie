using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kb.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kb.Tests.Integration;

/// <summary>
/// The AI's calls carry headers a plugin added (an access gateway's secret). Over real sockets: a model server that answers with a
/// redirect to another host gets no second call, the other host never sees the header, and no log line holds its value.
/// </summary>
public sealed class AiRedirectTests : IAsyncLifetime
{
    private const string Secret = "gateway-secret-5f1c9e";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Gateway : IOutboundRequestDecorator
    {
        public Task DecorateAsync(OutboundRequest request, CancellationToken ct)
        {
            request.Message.Headers.Add("X-Gateway-Secret", Secret);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every log line of the app, at every level.</summary>
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);
        public void Dispose() { }
        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { lines.Enqueue($"{category} scope {state}"); return null; }
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception}");
        }
    }

    /// <summary>A tiny HTTP server: answers every request with the given reply, and keeps the requests it read.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public ConcurrentQueue<string> Requests { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Server(Func<string> reply)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch (ObjectDisposedException) { return; } catch (SocketException) { return; }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[16384];
                        var read = await stream.ReadAsync(buffer);
                        Requests.Enqueue(Encoding.ASCII.GetString(buffer, 0, read));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply()));
                    }
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    private readonly Capture _logs = new();
    private Server _target = null!;
    private Server _moved = null!;
    private KbAppFactory _f = null!;

    public ValueTask InitializeAsync()
    {
        _target = new Server(() => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        _moved = new Server(() => $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{_target.Port}/v1/chat/completions\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var logs = _logs;
        _f = new KbAppFactory(KbAppFactory.NewDbPath(), settings: new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Trace" }, services: s =>
        {
            s.AddSingleton<IOutboundRequestDecorator, Gateway>();
            s.AddSingleton<ILoggerProvider>(logs);
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _f.DisposeAsync();
        _target.Dispose();
        _moved.Dispose();
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed_TheAddedHeaderGoesNowhereElse_AndIsNeverLogged()
    {
        var admin = await _f.SignedInBreakGlassAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/ai", new { settings = new
        {
            enabled = true, baseUrl = $"http://127.0.0.1:{_moved.Port}", model = "m", timeoutSeconds = 10, maxTokens = 100, temperature = 0, reasoning = false, ruleset = Kb.Core.Ai.AiGuard.DefaultRuleset,
            maxInputChars = 200000, userPerMinute = 10, tokenPerMinute = 10, importConcurrency = 1, features = new { import = "Off", format = "Off", search = "Off" },
        } }, Ct)).EnsureSuccessStatusCode();

        var test = await (await admin.PostAsync("/api/v1/admin/ai/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(test.GetProperty("ok").GetBoolean());
        Assert.Contains("redirect (302)", test.GetProperty("error").GetString(), StringComparison.Ordinal);

        var models = await admin.PostAsJsonAsync("/api/v1/admin/ai/models", new { baseUrl = $"http://127.0.0.1:{_moved.Port}" }, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, models.StatusCode);
        Assert.Contains("redirect", await models.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The first host got the header (the decorator ran); the host it pointed to got nothing at all.
        Assert.Equal(2, _moved.Requests.Count);
        Assert.All(_moved.Requests, r => Assert.Contains($"X-Gateway-Secret: {Secret}", r, StringComparison.Ordinal));
        Assert.Empty(_target.Requests);
        // Every log line, at Trace too: the header's name may appear, its value never.
        Assert.Contains(_logs.Lines, l => l.Contains("System.Net.Http.HttpClient.kb.ai", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
    }
}
