namespace Kb.Plugins;

/// <summary>Where uploaded bytes live. A key is a file id; the store decides the location.</summary>
public interface IFileStore
{
    Task SaveAsync(string key, byte[] data, CancellationToken ct);
    /// <summary>Streams to storage without holding the file in memory. Returns the bytes stored, or -1 (and stores nothing) when the source is over maxBytes.</summary>
    Task<long> SaveAsync(string key, Stream source, long maxBytes, CancellationToken ct);
    Task<Stream?> OpenAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    /// <summary>The stored size in bytes, or null when the file is not there.</summary>
    Task<long?> SizeAsync(string key, CancellationToken ct);
}

/// <summary>One setting of a storage kind. Type is "text" or "secret"; a secret is stored protected and never returned.</summary>
public sealed record FileStoreField(string Key, string Label, string Type = "text");

/// <summary>A setting of a storage place or a log target is missing or wrong. The message is shown beside that field.</summary>
public sealed class SettingException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>A kind of storage place an Admin can add, for example a folder or a cloud container.</summary>
public interface IFileStoreKind
{
    string Kind { get; }
    string Label { get; }
    IReadOnlyList<FileStoreField> Fields { get; }
    /// <summary>
    /// Opens a place with its settings, secrets already readable. Must not touch the storage itself.
    /// Throws <see cref="SettingException"/> when a setting is missing or wrong.
    /// </summary>
    IFileStore Open(IReadOnlyDictionary<string, string?> settings);
}
