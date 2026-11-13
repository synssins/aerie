using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Kb.Plugins;
using Microsoft.Win32.SafeHandles;

namespace Kb.Web;

/// <summary>A folder: on the web server, on an SMB share, on an NFS mount or on Azure Files. A relative path is under the content root.</summary>
public sealed class FolderFileStoreKind(IWebHostEnvironment env) : IFileStoreKind
{
    public const string Name = "folder";
    public string Kind => Name;
    public string Label => "Folder or network share";
    public IReadOnlyList<FileStoreField> Fields { get; } = [new("path", "Path"), new("userName", "User name"), new("password", "Password", "secret")];

    public IFileStore Open(IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = settings.GetValueOrDefault("path")?.Trim();
        if (string.IsNullOrEmpty(path)) throw new FileStoreSettingException("path", "Required.");
        var user = settings.GetValueOrDefault("userName")?.Trim();
        if (!string.IsNullOrEmpty(user) && !OperatingSystem.IsWindows()) throw new FileStoreSettingException("userName", "Windows only.");
        return new FolderFileStore(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path), user, settings.GetValueOrDefault("password"));
    }
}

/// <summary>
/// Files in a folder. With no user name it works as the account the site runs as. With one, it signs in to Windows for
/// network use only and runs each file operation as that sign-in; access to local disks is unaffected.
/// </summary>
public sealed class FolderFileStore(string root, string? userName = null, string? password = null) : IFileStore, IDisposable
{
    private readonly Lock _gate = new();
    private SafeAccessTokenHandle? _token;

    // Keys are generated GUIDs ("N" format), never user input, so they cannot escape the folder.
    private string PathOf(string key) => Path.Combine(root, key[..2], key);

    public Task SaveAsync(string key, byte[] data, CancellationToken ct) => RunAsync(async () =>
    {
        var p = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, data, ct);
        return true;
    });

    public Task<long> SaveAsync(string key, Stream source, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RunAsync(async () =>
        {
            var p = PathOf(key);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            long total = 0;
            try
            {
                await using var file = File.Create(p);
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > maxBytes) break;
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            catch { File.Delete(p); throw; }
            if (total <= maxBytes) return total;
            File.Delete(p);
            return -1;
        });
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct) =>
        RunAsync(() => Task.FromResult<Stream?>(File.Exists(PathOf(key)) ? File.OpenRead(PathOf(key)) : null));

    public Task DeleteAsync(string key, CancellationToken ct) => RunAsync(() =>
    {
        try { File.Delete(PathOf(key)); }
        catch (DirectoryNotFoundException) { } // never stored here
        return Task.FromResult(true);
    });

    public Task<long?> SizeAsync(string key, CancellationToken ct) => RunAsync(() =>
    {
        // FileInfo.Exists answers false for an unreachable share; Length says why.
        try { return Task.FromResult<long?>(new FileInfo(PathOf(key)).Length); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Task.FromResult<long?>(null); }
    });

    private Task<T> RunAsync<T>(Func<Task<T>> work) =>
        string.IsNullOrEmpty(userName) || !OperatingSystem.IsWindows() ? work() : WindowsIdentity.RunImpersonatedAsync(Token(), work);

    /// <summary>Signs in once and keeps the token. Windows does not check the password here; a wrong one fails at the first file operation.</summary>
    [SupportedOSPlatform("windows")]
    private SafeAccessTokenHandle Token()
    {
        lock (_gate)
        {
            if (_token is not null) return _token;
            var slash = userName!.IndexOf('\\', StringComparison.Ordinal);
            var (domain, user) = slash < 0 ? (null, userName) : (userName[..slash], userName[(slash + 1)..]);
            if (!LogonUser(user, domain, password ?? "", LogonNewCredentials, ProviderWinNt50, out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return _token = token;
        }
    }

    public void Dispose() => _token?.Dispose();

    private const int LogonNewCredentials = 9; // the sign-in is used for network connections only
    private const int ProviderWinNt50 = 3;

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}
