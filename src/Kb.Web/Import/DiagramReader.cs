using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Ai;

namespace Kb.Web.Import;

/// <summary>
/// Reads the structure of a diagram in a picture of an AI import (contract 0.10.0), for the draft contributors. One request per picture, apart
/// from the drafting: a small model answers a short, single task more reliably than one more part of the drafting answer. Whatever the
/// model answers is untrusted: it is read leniently and bounded (<see cref="DiagramLimits"/>) before a plugin sees it, and never fails the import.
/// </summary>
public static class DiagramReader
{
    /// <summary>At most this many pictures of one item are read for a diagram: each is a call to the model.</summary>
    public const int MaxPictures = 4;

    public const string Task =
        "Look at the picture. Is it a diagram: boxes, frames or shapes joined by lines, as drawn on a whiteboard, on paper or in a drawing tool? " +
        "If not, answer \"diagram\": false and empty lists. If it is: list every box once, with its text exactly as written: \"label\" its first line, " +
        "\"detail\" a second line if it has one (else \"\"), \"shape\" one of box, ellipse, cylinder, note (note for a sticky note or a written remark). " +
        "List every frame that encloses boxes as a container, with its label. \"in\" is the id of the container a box or container sits in, or \"\". " +
        "List every line once: \"from\" and \"to\" are ids of boxes or containers, \"label\" the text on the line (else \"\"), \"arrow\" none, forward " +
        "(a head at \"to\") or both, \"dashed\" and \"thick\" true or false. List text that belongs to no box under \"texts\", with \"kind\" title (the diagram's heading), remark (a short comment written next to one box or container; " +
        "\"beside\" is its id) or note (anything else, such as a to-do line), and put the title in \"title\" too (else \"\"). " +
        "Give ids b1, b2... to boxes and c1, c2... to containers. Give each box's and container's position \"x\", \"y\" (top left) and size \"w\", \"h\" " +
        "in percent of the picture, 0 to 100. Copy text exactly; do not guess what you cannot read.";

    public static JsonObject Schema()
    {
        static JsonObject Str() => new() { ["type"] = "string" };
        static JsonObject Num() => new() { ["type"] = "number" };
        static JsonObject Bool() => new() { ["type"] = "boolean" };
        static JsonObject List(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
        static JsonObject Obj(JsonObject props, params string[] required) => new() { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]) };
        return Obj(new JsonObject
        {
            ["diagram"] = Bool(),
            ["title"] = Str(),
            ["boxes"] = List(Obj(new JsonObject { ["id"] = Str(), ["label"] = Str(), ["detail"] = Str(), ["shape"] = Str(), ["in"] = Str(), ["x"] = Num(), ["y"] = Num(), ["w"] = Num(), ["h"] = Num() }, "id", "label")),
            ["containers"] = List(Obj(new JsonObject { ["id"] = Str(), ["label"] = Str(), ["in"] = Str(), ["x"] = Num(), ["y"] = Num(), ["w"] = Num(), ["h"] = Num() }, "id", "label")),
            ["lines"] = List(Obj(new JsonObject { ["from"] = Str(), ["to"] = Str(), ["label"] = Str(), ["arrow"] = Str(), ["dashed"] = Bool(), ["thick"] = Bool() }, "from", "to")),
            ["texts"] = List(Obj(new JsonObject { ["text"] = Str(), ["kind"] = Str(), ["in"] = Str(), ["beside"] = Str() }, "text")),
        }, "diagram", "boxes", "containers", "lines");
    }

    /// <summary>
    /// The diagram in the model's answer, bounded; null when the answer says it is not a diagram or holds nothing usable. Ids are matched
    /// leniently (an end named by its label is found too); what does not resolve or fit is dropped.
    /// </summary>
    public static PictureDiagram? Read(JsonNode? answer)
    {
        if (answer is not JsonObject root || !Flag(root["diagram"])) return null;

        // Containers first: their ids and labels are what boxes, lines and texts point to.
        var containers = new List<(string Id, string Label, string? In, DiagramArea? Area)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Items(root["containers"]).Take(DiagramLimits.Containers))
        {
            if (Text(c["label"], DiagramLimits.Label) is not { } label) continue;
            containers.Add((Unique(Text(c["id"], DiagramLimits.Id), "c", ids), label, Text(c["in"], DiagramLimits.Id), Area(c)));
        }
        var boxes = new List<(string Id, string Label, string? Detail, DiagramShape Shape, string? In, DiagramArea? Area)>();
        foreach (var b in Items(root["boxes"]).Take(DiagramLimits.Boxes))
        {
            if (Text(b["label"], DiagramLimits.Label) is not { } label) continue;
            boxes.Add((Unique(Text(b["id"], DiagramLimits.Id), "b", ids), label, Text(b["detail"], DiagramLimits.Detail), Shape(Text(b["shape"], 40)), Text(b["in"], DiagramLimits.Id), Area(b)));
        }

        // A weak model often lists a frame as a box and puts other boxes "in" it: a box that something sits in is a container.
        var holders = boxes.Select(x => x.In).Concat(containers.Select(x => x.In)).Concat(Items(root["texts"]).Select(t => Text(t["in"], DiagramLimits.Id)))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var box in boxes.Where(x => holders.Contains(x.Id) || holders.Contains(x.Label)).ToList())
        {
            if (containers.Count == DiagramLimits.Containers) break;
            boxes.Remove(box);
            containers.Add((box.Id, box.Label, box.In, box.Area));
        }

        // A name resolves to an id: as an id, else as a label (a weak model names ends by their text).
        var byLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in containers) byLabel.TryAdd(c.Label, c.Id);
        foreach (var b in boxes) byLabel.TryAdd(b.Label, b.Id);
        var containerIds = containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        string? Resolve(string? name, bool containerOnly)
        {
            if (name is null) return null;
            var id = ids.Contains(name) ? name : byLabel.GetValueOrDefault(name);
            return id is null || (containerOnly && !containerIds.Contains(id)) ? null : id;
        }

        // Nesting: a container's parent must resolve, and the chain must end within the depth, without a loop.
        var parents = containers.ToDictionary(c => c.Id, c => Resolve(c.In, containerOnly: true) is { } p && p != c.Id ? p : null, StringComparer.Ordinal);
        foreach (var id in parents.Keys.ToList())
        {
            var (at, depth, seen) = (parents[id], 1, new HashSet<string>(StringComparer.Ordinal) { id });
            while (at is not null && depth <= DiagramLimits.Depth && seen.Add(at)) (at, depth) = (parents.GetValueOrDefault(at), depth + 1);
            if (at is not null) parents[id] = null; // a loop, or too deep: the container stands on its own
        }

        var lines = new List<DiagramLine>();
        foreach (var l in Items(root["lines"]))
        {
            if (lines.Count == DiagramLimits.Lines) break;
            if (Resolve(Text(l["from"], DiagramLimits.Label), false) is not { } from || Resolve(Text(l["to"], DiagramLimits.Label), false) is not { } to) continue;
            lines.Add(new DiagramLine(from, to, Text(l["label"], DiagramLimits.Label), Arrow(Text(l["arrow"], 20)), Flag(l["dashed"]), Flag(l["thick"])));
        }
        var texts = new List<DiagramText>();
        var title = Text(root["title"], DiagramLimits.Label);
        foreach (var t in Items(root["texts"]).Take(DiagramLimits.Texts))
        {
            if (Text(t["text"], DiagramLimits.Text) is not { } text) continue;
            var kind = Kind(Text(t["kind"], 20));
            var beside = kind == DiagramTextKind.Remark ? Resolve(Text(t["beside"], DiagramLimits.Label), containerOnly: false) : null;
            texts.Add(new DiagramText(text, Resolve(Text(t["in"], DiagramLimits.Id), containerOnly: true), Area(t)) { Kind = kind, Beside = beside });
        }
        // The title in its own field, or the first text the AI called a title; it is among the texts either way.
        title ??= texts.FirstOrDefault(t => t.Kind == DiagramTextKind.Title)?.Text is { } first ? first[..Math.Min(first.Length, DiagramLimits.Label)] : null;
        if (title is not null && !texts.Any(t => t.Text == title) && texts.Count < DiagramLimits.Texts) texts.Insert(0, new DiagramText(title, null, null) { Kind = DiagramTextKind.Title });

        if (boxes.Count == 0 && containers.Count == 0) return null;
        return new PictureDiagram(
            [.. boxes.Select(b => new DiagramBox(b.Id, b.Label, b.Detail, b.Shape, Resolve(b.In, containerOnly: true), b.Area))],
            [.. containers.Select(c => new DiagramContainer(c.Id, c.Label, parents[c.Id], c.Area))],
            lines, texts) { Title = title };
    }

    /// <summary>Every label and text of the diagram, for the check on text addressed to an AI.</summary>
    public static IReadOnlyDictionary<string, string> Texts(PictureDiagram d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new Dictionary<string, string>
        {
            ["diagram"] = string.Join("\n", d.Boxes.SelectMany(b => new[] { b.Label, b.Detail }).Concat(d.Containers.Select(c => c.Label))
                .Concat(d.Lines.Select(l => l.Label)).Concat(d.Texts.Select(t => t.Text)).OfType<string>()),
        };
    }

    private static IEnumerable<JsonObject> Items(JsonNode? list) => list is JsonArray a ? a.OfType<JsonObject>() : [];

    private static string? Text(JsonNode? node, int max)
    {
        var raw = node is JsonValue v ? v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => v.ToJsonString(),
            _ => null,
        } : null;
        var text = raw is null ? null : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private static bool Flag(JsonNode? node) =>
        node is JsonValue v && (v.GetValueKind() == System.Text.Json.JsonValueKind.True || v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>().Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string Unique(string? id, string prefix, HashSet<string> ids)
    {
        if (id is not null && ids.Add(id)) return id;
        for (var i = ids.Count + 1; ; i++)
            if (ids.Add(prefix + i.ToString(CultureInfo.InvariantCulture))) return prefix + i.ToString(CultureInfo.InvariantCulture);
    }

    private static DiagramShape Shape(string? s) => s?.ToLowerInvariant() switch
    {
        "ellipse" or "oval" or "circle" or "cloud" => DiagramShape.Ellipse,
        "cylinder" or "database" or "db" or "disk" => DiagramShape.Cylinder,
        "note" or "sticky" or "sticky note" or "text" => DiagramShape.Note,
        _ => DiagramShape.Box,
    };

    private static DiagramTextKind Kind(string? s) => s?.ToLowerInvariant() switch
    {
        "title" or "heading" => DiagramTextKind.Title,
        "remark" or "comment" or "label" or "callout" => DiagramTextKind.Remark,
        _ => DiagramTextKind.Note,
    };

    private static DiagramArrow Arrow(string? s) => s?.ToLowerInvariant() switch
    {
        "forward" or "one" or "one-way" or "to" or "->" or "single" => DiagramArrow.Forward,
        "both" or "two" or "two-way" or "<->" or "double" => DiagramArrow.Both,
        _ => DiagramArrow.None,
    };

    /// <summary>
    /// The area in fractions: given in percent (0 to 100), or in fractions when all four are at most 1. Null when one is missing,
    /// not a number, or the area does not lie inside the picture.
    /// </summary>
    private static DiagramArea? Area(JsonObject item)
    {
        double? N(string k) => item[k] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        if (N("x") is not { } x || N("y") is not { } y || N("w") is not { } w || N("h") is not { } h) return null;
        var scale = x <= 1 && y <= 1 && w <= 1 && h <= 1 ? 1 : 100;
        (x, y, w, h) = (x / scale, y / scale, w / scale, h / scale);
        return x >= 0 && y >= 0 && w > 0 && h > 0 && x + w <= 1.0001 && y + h <= 1.0001 ? new DiagramArea(x, y, Math.Min(w, 1 - x), Math.Min(h, 1 - y)) : null;
    }
}
