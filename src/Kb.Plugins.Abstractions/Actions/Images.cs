using System.Text.Json.Serialization;

namespace Kb.Plugins.Actions;

/// <summary>Stores an image in a space, for its pages to show. PNG, JPEG, GIF or WebP, 20 MB at most: a larger file is refused before any hook runs.</summary>
[Action("image.add")]
public sealed record AddImage : IAction<ImageDto>
{
    public required Guid SpaceId { get; init; }
    [JsonIgnore] public required UploadContent File { get; init; }
}

/// <summary>Copies an image from another site into a space, so a pasted page keeps its pictures.</summary>
[Action("image.import")]
public sealed record ImportImage : IAction<ImageDto>
{
    public required Guid SpaceId { get; init; }
    /// <summary>An http or https address on the public internet.</summary>
    public required string Url { get; init; }
}

[Action("image.read", Read = true)]
public sealed record ReadImage : IAction<FileOut>
{
    public required Guid ImageId { get; init; }
}

public sealed record ImageDto(Guid Id, string Url);
