using System.Text.Json.Serialization;

namespace Kb.Plugins.Actions;

/// <summary>Attaches a file to a page. The file goes straight to the store; it is never held whole in memory.</summary>
[Action("attachment.add")]
public sealed record AddAttachment : IAction<AttachmentDto>
{
    public required Guid PageId { get; init; }
    /// <summary>Needed. It is not "required" because System.Text.Json refuses a type with a required member under [JsonIgnore].</summary>
    [JsonIgnore] public UploadContent? File { get; init; }
}

/// <summary>The file of an attachment. Always a download, never shown inline: an attachment can be any file type.</summary>
[Action("attachment.read", Read = true)]
public sealed record ReadAttachment : IAction<FileOut>
{
    public required Guid AttachmentId { get; init; }
}

/// <summary>Removes an attachment from its page. The file itself is kept.</summary>
[Action("attachment.remove")]
public sealed record RemoveAttachment : IAction<Unit>
{
    public required Guid AttachmentId { get; init; }
}

[Action("settings.uploads.read", Read = true)]
public sealed record ReadUploadSettings : IAction<UploadSettingsDto>;

/// <summary>Sets the largest attachment, in MB. Saved even when it is above the web server's limit: the smaller limit applies until the server's is raised.</summary>
[Action("settings.uploads.save")]
public sealed record SaveUploadSettings : IAction<UploadSettingsDto>
{
    public required int AttachmentMb { get; init; }
}

public sealed record UploadSettingsDto(int AttachmentMb, long? ServerLimitBytes);
