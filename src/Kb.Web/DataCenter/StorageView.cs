using System.Net;
using System.Text;

namespace Kb.Web.DataCenter;

/// <summary>What rendering needs to look up: pages, users and spaces a body points at. Null for one the reader cannot see.</summary>
public interface IStorageLinks
{
    /// <summary>A page by title, in the named space or the current one.</summary>
    Task<string?> PageUrlAsync(string? spaceKey, string title);
    Task<string?> ContentUrlAsync(string id);
    Task<string?> SpaceUrlAsync(string spaceKey);
    Task<string?> UserNameAsync(string? userKey, string? userName);
    /// <summary>The child pages of the page being rendered, for the children macro: (title, url).</summary>
    Task<IReadOnlyList<(string Title, string Url)>> ChildrenAsync();
}

/// <summary>
/// Storage to HTML for the view representations (representations.md, Rendering for view). The markup is aerie's own; headings keep their
/// level, lists and tables their nesting, links and images resolve, the T1 macros render, and anything else shows as a placeholder box
/// naming it, with its text inside.
/// </summary>
public static class StorageView
{
    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "hr", "br", "strong", "b", "em", "i", "u", "s", "del", "sub", "sup", "code", "small", "big",
        "span", "a", "ul", "ol", "li", "table", "colgroup", "col", "thead", "tbody", "tr", "th", "td",
    };
    private static readonly HashSet<string> Rendered = new(StringComparer.Ordinal)
    {
        "code", "noformat", "info", "note", "warning", "tip", "panel", "expand", "toc", "anchor", "status", "children", "excerpt", "excerpt-include", "include",
    };
    private static readonly Dictionary<string, string> Emoticons = new(StringComparer.Ordinal)
    {
        ["smile"] = "🙂", ["sad"] = "🙁", ["cheeky"] = "😛", ["laugh"] = "😄", ["wink"] = "😉", ["thumbs-up"] = "👍", ["thumbs-down"] = "👎",
        ["information"] = "ℹ️", ["tick"] = "✔️", ["cross"] = "❌", ["warning"] = "⚠️",
    };

    /// <param name="baseUrl">Prefix for aerie's own paths: empty for view, the site's absolute URL for the export forms.</param>
    public static async Task<string> RenderAsync(string storage, IStorageLinks links, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(links);
        var nodes = StorageFormat.Parse(storage ?? "");
        var sb = new StringBuilder();
        var r = new Renderer(links, baseUrl ?? "", nodes);
        foreach (var n in nodes) await r.NodeAsync(n, sb);
        return sb.ToString();
    }

    /// <summary>export_view as a whole HTML document with its styles inline.</summary>
    public static string Styled(string exportView, string title) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\" /><title>" + WebUtility.HtmlEncode(title) + "</title><style>" +
        "body{font-family:sans-serif;line-height:1.5;margin:2em}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px}" +
        "pre{background:#f4f5f7;padding:8px;overflow:auto}.aerie-panel{border-left:4px solid #8a94a6;padding:4px 12px;margin:8px 0}" +
        ".aerie-panel-info{border-color:#2f6fd1}.aerie-panel-note{border-color:#7b5fc8}.aerie-panel-warning{border-color:#d14a2f}.aerie-panel-tip{border-color:#2f9a5b}" +
        ".aerie-opaque{border:1px dashed #8a94a6;padding:4px 8px;margin:8px 0;color:#555}.aerie-unresolved{color:#b00}.aerie-status{border:1px solid;padding:0 4px;border-radius:3px}" +
        "</style></head><body>" + exportView + "</body></html>";

    private sealed class Renderer(IStorageLinks links, string baseUrl, IReadOnlyList<SNode> root)
    {
        private string Local(string path) => path.StartsWith('/') ? baseUrl + path : path;

        public async Task NodeAsync(SNode n, StringBuilder sb)
        {
            switch (n)
            {
                case SText t: sb.Append(WebUtility.HtmlEncode(t.Value)); return;
                case SRawOnly r when r.Raw.StartsWith("<![CDATA[", StringComparison.Ordinal): sb.Append(WebUtility.HtmlEncode(r.Value)); return;
                case SRawOnly: return;
                case SElem e: await ElemAsync(e, sb); return;
            }
        }

        private async Task KidsAsync(IEnumerable<SNode> kids, StringBuilder sb)
        {
            foreach (var k in kids) await NodeAsync(k, sb);
        }

        private async Task ElemAsync(SElem e, StringBuilder sb)
        {
            if (Plain.Contains(e.Name))
            {
                sb.Append('<').Append(e.Name);
                foreach (var (name, value) in e.Attrs)
                {
                    var keep = name switch
                    {
                        "href" when e.Name == "a" => SafeHref(value) is { } href ? Local(href) : null,
                        "colspan" or "rowspan" or "span" => value,
                        "style" => SafeStyle(value), // alignment, indents, colours and widths are what the author set; nothing else is kept
                        _ => null,
                    };
                    if (keep is not null) sb.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(keep)).Append('"');
                }
                if (e.Name is "hr" or "br" or "col") { sb.Append(" />"); return; }
                sb.Append('>');
                await KidsAsync(e.Kids, sb);
                sb.Append("</").Append(e.Name).Append('>');
                return;
            }
            switch (e.Name)
            {
                case "ac:link": await LinkAsync(e, sb); return;
                case "ac:image": await ImageAsync(e, sb); return;
                case "ac:emoticon":
                    sb.Append("<span class=\"aerie-emoticon\">").Append(Emoticons.GetValueOrDefault(e.Attr("ac:name") ?? "", "")).Append("</span>");
                    return;
                case "ac:task-list":
                    sb.Append("<ul class=\"aerie-tasks\">");
                    foreach (var task in e.Elems.Where(x => x.Name == "ac:task"))
                    {
                        var done = task.Elems.FirstOrDefault(x => x.Name == "ac:task-status")?.Text == "complete";
                        sb.Append("<li><input type=\"checkbox\" disabled=\"disabled\"").Append(done ? " checked=\"checked\"" : "").Append(" /> ");
                        if (task.Elems.FirstOrDefault(x => x.Name == "ac:task-body") is { } body) await KidsAsync(body.Kids, sb);
                        sb.Append("</li>");
                    }
                    sb.Append("</ul>");
                    return;
                case "ac:placeholder": return; // shown only in the editor
                case "ac:layout" or "ac:layout-section" or "ac:layout-cell":
                    var cls = e.Name[3..] + (e.Attr("ac:type") is { } type ? " aerie-" + WebUtility.HtmlEncode(type) : "");
                    sb.Append("<div class=\"aerie-").Append(cls).Append("\">");
                    await KidsAsync(e.Kids, sb);
                    sb.Append("</div>");
                    return;
                case "ac:structured-macro" when Rendered.Contains(e.Attr("ac:name") ?? ""):
                    await MacroAsync(e, sb);
                    return;
            }
            Placeholder(e, sb);
        }

        private static readonly HashSet<string> StyleNames = new(StringComparer.OrdinalIgnoreCase) { "text-align", "margin-left", "color", "background-color", "width" };

        /// <summary>The declarations of a style that only place or colour text. One that could fetch or cover something (url(, position, an expression) is dropped.</summary>
        private static string? SafeStyle(string style)
        {
            var kept = style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => d.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(d => d.Length == 2 && StyleNames.Contains(d[0]) && d[1].All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '#' or '.' or '%' or '-' or ',' or '(' or ')') && !d[1].Contains("url", StringComparison.OrdinalIgnoreCase)
                    && !d[1].Contains("expression", StringComparison.OrdinalIgnoreCase))
                .Select(d => $"{d[0]}: {d[1]};").ToList();
            return kept.Count == 0 ? null : string.Join(' ', kept);
        }

        private static string? SafeHref(string href)
        {
            var h = href.Trim();
            if (h.StartsWith('#') || h.StartsWith('/')) return h;
            return Uri.TryCreate(h, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto" ? h : null;
        }

        /// <summary>Anything aerie does not render: a box naming it, with its text inside.</summary>
        private static void Placeholder(SElem e, StringBuilder sb)
        {
            var name = e.Name == "ac:structured-macro" ? e.Attr("ac:name") ?? e.Name : e.Name;
            sb.Append("<div class=\"aerie-opaque\" data-name=\"").Append(WebUtility.HtmlEncode(name)).Append("\"><strong>").Append(WebUtility.HtmlEncode(name)).Append("</strong>");
            var text = e.Text.Trim();
            if (text.Length > 0) sb.Append(' ').Append(WebUtility.HtmlEncode(text));
            sb.Append("</div>");
        }

        private async Task<(string? Url, string Label)> TargetAsync(SElem? r)
        {
            if (r is null) return (null, "");
            switch (r.Name)
            {
                case "ri:page" or "ri:blog-post":
                    var title = r.Attr("ri:content-title") ?? "";
                    return (await links.PageUrlAsync(r.Attr("ri:space-key"), title), title);
                case "ri:url": return (r.Attr("ri:value") is { } v ? SafeHref(v) : null, r.Attr("ri:value") ?? "");
                case "ri:space":
                    var key = r.Attr("ri:space-key") ?? "";
                    return (await links.SpaceUrlAsync(key), key);
                case "ri:content-entity":
                    var id = r.Attr("ri:content-id") ?? "";
                    return (await links.ContentUrlAsync(id), id);
                case "ri:user":
                    return (null, await links.UserNameAsync(r.Attr("ri:userkey"), r.Attr("ri:username")) ?? "");
                case "ri:attachment":
                    return (null, r.Attr("ri:filename") ?? "");
                default: return (null, "");
            }
        }

        private async Task LinkAsync(SElem e, StringBuilder sb)
        {
            var target = e.Elems.FirstOrDefault(x => x.Name.StartsWith("ri:", StringComparison.Ordinal));
            var (url, label) = await TargetAsync(target);
            if (target is null && e.Attr("ac:anchor") is { } anchor) (url, label) = ("#" + anchor, anchor);
            else if (url is not null && e.Attr("ac:anchor") is { } a) url += "#" + a;
            var body = e.Elems.FirstOrDefault(x => x.Name is "ac:link-body" or "ac:plain-text-link-body");
            sb.Append(url is null ? "<span class=\"aerie-unresolved\">" : "<a href=\"" + WebUtility.HtmlEncode(Local(url)) + "\">");
            if (body is { Name: "ac:link-body" }) await KidsAsync(body.Kids, sb);
            else sb.Append(WebUtility.HtmlEncode(body?.Text is { Length: > 0 } text ? text : label));
            sb.Append(url is null ? "</span>" : "</a>");
        }

        private async Task ImageAsync(SElem e, StringBuilder sb)
        {
            var (url, label) = await TargetAsync(e.Elems.FirstOrDefault(x => x.Name.StartsWith("ri:", StringComparison.Ordinal)));
            if (url is null) { sb.Append("<span class=\"aerie-unresolved\">").Append(WebUtility.HtmlEncode(label)).Append("</span>"); return; }
            sb.Append("<img src=\"").Append(WebUtility.HtmlEncode(Local(url))).Append('"');
            foreach (var (attr, html) in new[] { ("ac:alt", "alt"), ("ac:title", "title"), ("ac:width", "width"), ("ac:height", "height") })
                if (e.Attr(attr) is { } v) sb.Append(' ').Append(html).Append("=\"").Append(WebUtility.HtmlEncode(v)).Append('"');
            sb.Append(" />");
        }

        private async Task MacroAsync(SElem e, StringBuilder sb)
        {
            var (name, _, parameters) = StorageFormat.Describe(e);
            var rich = e.Elems.FirstOrDefault(x => x.Name == "ac:rich-text-body");
            var plain = e.Elems.FirstOrDefault(x => x.Name == "ac:plain-text-body")?.Text ?? "";
            async Task Rich() { if (rich is not null) await KidsAsync(rich.Kids, sb); }
            switch (name)
            {
                case "code" or "noformat":
                    sb.Append("<pre><code");
                    if (parameters.GetValueOrDefault("language") is { Length: > 0 } language) sb.Append(" class=\"language-").Append(WebUtility.HtmlEncode(language)).Append('"');
                    sb.Append('>').Append(WebUtility.HtmlEncode(plain)).Append("</code></pre>");
                    return;
                case "info" or "note" or "warning" or "tip" or "panel":
                    sb.Append("<div class=\"aerie-panel aerie-panel-").Append(name).Append("\">");
                    if (parameters.GetValueOrDefault("title") is { Length: > 0 } title) sb.Append("<p><strong>").Append(WebUtility.HtmlEncode(title)).Append("</strong></p>");
                    await Rich();
                    sb.Append("</div>");
                    return;
                case "expand":
                    sb.Append("<details><summary>").Append(WebUtility.HtmlEncode(parameters.GetValueOrDefault("title") is { Length: > 0 } t ? t : "Click here to expand...")).Append("</summary>");
                    await Rich();
                    sb.Append("</details>");
                    return;
                case "excerpt": await Rich(); return;
                case "anchor":
                    var anchor = parameters.GetValueOrDefault("") ?? parameters.Values.FirstOrDefault() ?? "";
                    sb.Append("<a id=\"").Append(WebUtility.HtmlEncode(anchor)).Append("\"></a>");
                    return;
                case "status":
                    var colour = parameters.GetValueOrDefault("colour") ?? parameters.GetValueOrDefault("color") ?? "grey";
                    sb.Append("<span class=\"aerie-status aerie-status-").Append(WebUtility.HtmlEncode(colour.ToLowerInvariant())).Append("\">")
                        .Append(WebUtility.HtmlEncode(parameters.GetValueOrDefault("title") ?? "")).Append("</span>");
                    return;
                case "toc":
                    sb.Append("<ul class=\"aerie-toc\">");
                    foreach (var h in Headings(root)) sb.Append("<li class=\"aerie-toc-").Append(h.Name).Append("\">").Append(WebUtility.HtmlEncode(h.Text)).Append("</li>");
                    sb.Append("</ul>");
                    return;
                case "children":
                    sb.Append("<ul class=\"aerie-children\">");
                    foreach (var (childTitle, url) in await links.ChildrenAsync()) sb.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(Local(url))).Append("\">").Append(WebUtility.HtmlEncode(childTitle)).Append("</a></li>");
                    sb.Append("</ul>");
                    return;
                default: // include and excerpt-include: a link to the page they show
                    var page = e.Elems.SelectMany(x => x.Name == "ac:parameter" ? x.Elems : []).SelectMany(x => x.Name == "ac:link" ? x.Elems : [x]).FirstOrDefault(x => x.Name == "ri:page");
                    var (target, label) = await TargetAsync(page);
                    sb.Append("<div class=\"aerie-include\">");
                    sb.Append(target is null ? "<span class=\"aerie-unresolved\">" + WebUtility.HtmlEncode(label) + "</span>" : "<a href=\"" + WebUtility.HtmlEncode(Local(target)) + "\">" + WebUtility.HtmlEncode(label) + "</a>");
                    sb.Append("</div>");
                    return;
            }
        }

        private static IEnumerable<SElem> Headings(IEnumerable<SNode> nodes)
        {
            foreach (var e in nodes.OfType<SElem>())
            {
                if (e.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") yield return e;
                else foreach (var h in Headings(e.Kids)) yield return h;
            }
        }
    }
}
