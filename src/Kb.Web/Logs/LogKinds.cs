using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kb.Plugins;

namespace Kb.Web;

/// <summary>The one JSON form of a shipped entry: the same in the file, in a syslog message and in an HTTPS post.</summary>
public static class LogJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Line(LogEvent e) => JsonSerializer.Serialize(e, Options);
    public static string Array(IReadOnlyList<LogEvent> events) => JsonSerializer.Serialize(events, Options);
}

/// <summary>Local: one JSON line per entry in a file per day, which any SIEM agent can pick up. A relative folder is under the content root.</summary>
public sealed class FileLogShipperKind(IWebHostEnvironment env) : ILogShipperKind
{
    public const string Name = "file";
    public string Kind => Name;
    public string Label => "Log file";
    public IReadOnlyList<LogShipField> Fields { get; } = [new("path", "Folder"), new("days", "Days kept")];

    public ILogShipper Open(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = settings.GetValueOrDefault("path")?.Trim();
        if (string.IsNullOrEmpty(path)) throw new SettingException("path", "Required.");
        var days = 90;
        if (settings.GetValueOrDefault("days") is { Length: > 0 } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days is < 1 or > 3650))
            throw new SettingException("days", "1 to 3650.");
        return new FileLogShipper(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path), days);
    }
}

public sealed class FileLogShipper(string folder, int days) : ILogShipper
{
    private const string Pattern = "audit-*.jsonl";

    public async Task SendAsync(IReadOnlyList<LogEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        Directory.CreateDirectory(folder);
        foreach (var day in events.GroupBy(e => e.At.UtcDateTime.Date))
        {
            var file = Path.Combine(folder, $"audit-{day.Key:yyyy-MM-dd}.jsonl");
            var first = !File.Exists(file);
            await File.AppendAllLinesAsync(file, day.Select(LogJson.Line), ct);
            if (first) Prune();
        }
    }

    /// <summary>On the first entry of a day: removes the files of days older than "Days kept". The audit log in the database is the record; these files are a feed.</summary>
    private void Prune()
    {
        var oldest = DateTime.UtcNow.Date.AddDays(-days);
        foreach (var file in Directory.GetFiles(folder, Pattern))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name["audit-".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < oldest) File.Delete(file);
        }
    }
}

/// <summary>Syslog, RFC 5424, over UDP, TCP or TLS. The message text is the entry as JSON.</summary>
public sealed class SyslogShipperKind : ILogShipperKind
{
    private static readonly string[] Protocols = ["udp", "tcp", "tls"];
    public string Kind => "syslog";
    public string Label => "Syslog";
    public IReadOnlyList<LogShipField> Fields { get; } = [new("host", "Host"), new("port", "Port"), new("protocol", "Protocol", "select", Protocols)];

    public ILogShipper Open(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var host = settings.GetValueOrDefault("host")?.Trim();
        if (string.IsNullOrEmpty(host)) throw new SettingException("host", "Required.");
        var protocol = settings.GetValueOrDefault("protocol") is { Length: > 0 } p ? p : "udp";
        if (!Protocols.Contains(protocol)) throw new SettingException("protocol", "udp, tcp or tls.");
        var port = protocol == "tls" ? 6514 : 514;
        if (settings.GetValueOrDefault("port") is { Length: > 0 } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new SettingException("port", "1 to 65535.");
        return new SyslogShipper(host, port, protocol);
    }
}

public sealed class SyslogShipper(string host, int port, string protocol) : ILogShipper
{
    private const int Facility = 13; // log audit
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A failed or refused sign-in, a refused token, break-glass and emergency access are warnings; everything else is information.</summary>
    internal static string Format(LogEvent e)
    {
        var warning = e.Action.EndsWith(".failed", StringComparison.Ordinal) || e.Action.EndsWith(".refused", StringComparison.Ordinal)
            || e.Action is "auth.breakglass" or "space.sensitive.emergency";
        var pri = Facility * 8 + (warning ? 4 : 6);
        var time = e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var server = string.IsNullOrEmpty(e.Server) ? "-" : e.Server.Replace(' ', '-');
        return $"<{pri}>1 {time} {server} kb - {e.Action} - {LogJson.Line(e)}";
    }

    public async Task SendAsync(IReadOnlyList<LogEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (protocol == "udp")
        {
            // shortcut: one datagram per entry, not split; an entry larger than the network allows is dropped by the network. Use tcp or tls if entries grow.
            using var udp = new UdpClient();
            foreach (var e in events) await udp.SendAsync(Encoding.UTF8.GetBytes(Format(e)), host, port, ct);
            return;
        }

        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(ConnectTimeout);
            try { await tcp.ConnectAsync(host, port, connect.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"No answer from {host}:{port}."); }
        }
        Stream stream = tcp.GetStream();
        if (protocol == "tls")
        {
            // The certificate must be one this server trusts. There is no setting to skip the check.
            var tls = new SslStream(stream);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            stream = tls;
        }
        await using (stream)
        {
            foreach (var e in events)
            {
                var message = Encoding.UTF8.GetBytes(Format(e));
                // RFC 5425 (TLS) counts the bytes; plain TCP ends each message with a line feed, which collectors accept by default.
                var framed = protocol == "tls" ? [.. Encoding.ASCII.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " "), .. message] : (byte[])[.. message, (byte)'\n'];
                await stream.WriteAsync(framed, ct);
            }
            await stream.FlushAsync(ct);
        }
    }
}

/// <summary>An HTTPS collector: each batch is one POST of a JSON array. One header can carry a key.</summary>
public sealed partial class HttpsLogShipperKind(IHttpClientFactory clients) : ILogShipperKind
{
    public const string ClientName = "kb-logship";
    public string Kind => "https";
    public string Label => "HTTPS collector";
    public IReadOnlyList<LogShipField> Fields { get; } = [new("url", "Address"), new("headerName", "Header name"), new("headerValue", "Header value", "secret")];

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex HeaderName();

    public ILogShipper Open(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(settings.GetValueOrDefault("url")?.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new SettingException("url", "An https address.");
        var header = settings.GetValueOrDefault("headerName")?.Trim();
        if (!string.IsNullOrEmpty(header) && !HeaderName().IsMatch(header)) throw new SettingException("headerName", "Letters, digits and hyphens.");
        return new HttpsLogShipper(clients, url, header, settings.GetValueOrDefault("headerValue"));
    }
}

public sealed class HttpsLogShipper(IHttpClientFactory clients, Uri url, string? headerName, string? headerValue) : ILogShipper
{
    public async Task SendAsync(IReadOnlyList<LogEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(LogJson.Array(events), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(headerName)) request.Headers.TryAddWithoutValidation(headerName, headerValue ?? "");
        using var client = clients.CreateClient(HttpsLogShipperKind.ClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The collector answered HTTP {(int)response.StatusCode}.");
    }
}
