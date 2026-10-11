using System.Net;
using Markdig;

namespace Harness.Host;

/// <summary>
/// What <c>GET /api/teams/{team}/documents/view</c> answers for a file, by its
/// extension and nothing else. A document is whatever a person or an agent put there, so the
/// browser is told exactly what it is (<c>nosniff</c>) and the response runs in a sandbox with an
/// opaque origin (<see cref="SandboxPolicy"/>): an uploaded HTML page renders, but no script in it
/// runs, and it cannot read the session or call <c>/api</c> as the person.
/// </summary>
public static class DocumentView
{
    /// <summary>
    /// REPLACES the app's CSP on every view response except PDF's, which gets
    /// <see cref="PdfPolicy"/> (also sandboxed). <c>sandbox</c> without
    /// <c>allow-scripts</c> or <c>allow-same-origin</c> is the load-bearing part: no script, and an
    /// opaque origin. The rest lets a rendered page carry its own inline styles and data: images and
    /// fonts, and fetch nothing.
    /// </summary>
    public const string SandboxPolicy =
        "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:";

    /// <summary>
    /// PDF only: the same <c>sandbox</c> with <c>default-src 'none'</c> and nothing else, since a
    /// PDF needs no inline style or data: image of its own. Chrome's built-in viewer displays a PDF
    /// in a sandboxed response (confirmed in Chromium 153, headed and headless).
    /// </summary>
    public const string PdfPolicy = "sandbox; default-src 'none'";

    public enum Kind { Markdown, Html, Text, Image, Pdf }

    public sealed record Type(Kind Kind, string ContentType);

    private static readonly Dictionary<string, Type> ByExtension = Build();

    private static Dictionary<string, Type> Build()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in new[] { ".md", ".markdown" })
            map[ext] = new(Kind.Markdown, "text/html; charset=utf-8");

        foreach (var ext in new[] { ".html", ".htm" })
            map[ext] = new(Kind.Html, "text/html");

        foreach (var ext in new[]
                 {
                     ".txt", ".log", ".json", ".csv", ".yaml", ".yml", ".xml", ".toml",
                     ".cs", ".ts", ".js", ".vue", ".py", ".go", ".sh", ".ps1", ".sql",
                 })
            map[ext] = new(Kind.Text, "text/plain; charset=utf-8");

        map[".png"] = new(Kind.Image, "image/png");
        map[".jpg"] = new(Kind.Image, "image/jpeg");
        map[".jpeg"] = new(Kind.Image, "image/jpeg");
        map[".gif"] = new(Kind.Image, "image/gif");
        map[".webp"] = new(Kind.Image, "image/webp");
        map[".svg"] = new(Kind.Image, "image/svg+xml");
        map[".pdf"] = new(Kind.Pdf, "application/pdf");

        return map;
    }

    /// <summary>What <paramref name="fileName"/> is shown as, or null when it is not viewable (415).</summary>
    public static Type? For(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var type) ? type : null;

    /// <summary>The CSP a view response carries in place of the app's.</summary>
    public static string PolicyFor(Kind kind) => kind == Kind.Pdf ? PdfPolicy : SandboxPolicy;

    /// <summary>
    /// Markdown with raw HTML DISABLED, so a <c>&lt;script&gt;</c> in the source is escaped into
    /// text rather than passed through. The extensions are named one by one rather than
    /// <c>UseAdvancedExtensions</c>, which brings generic attributes: <c>{onclick=...}</c> on any
    /// element.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseGridTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoIdentifiers()
        .UseListExtras()
        .Build();

    /// <summary>A small readable page: a max width, system fonts, tables and code styled, light and dark.</summary>
    public static string RenderMarkdown(string fileName, string markdown) =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>{{WebUtility.HtmlEncode(fileName)}}</title>
        <style>{{Style}}</style>
        </head>
        <body>
        <main>
        {{Markdown.ToHtml(markdown, Pipeline)}}
        </main>
        </body>
        </html>
        """;

    private const string Style =
        """
        :root { color-scheme: light dark; --fg: #1f2328; --bg: #ffffff; --muted: #59636e; --line: #d1d9e0; --code: #f6f8fa; --link: #0969da; }
        @media (prefers-color-scheme: dark) { :root { --fg: #e6edf3; --bg: #0d1117; --muted: #9198a1; --line: #3d444d; --code: #151b23; --link: #4493f8; } }
        html { background: var(--bg); color: var(--fg); }
        body { margin: 0; font: 16px/1.6 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
        main { max-width: 50rem; margin: 0 auto; padding: 2rem 1.25rem 4rem; overflow-wrap: break-word; }
        h1, h2 { border-bottom: 1px solid var(--line); padding-bottom: .3em; }
        a { color: var(--link); }
        code, pre { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace; font-size: .9em; background: var(--code); border-radius: 6px; }
        code { padding: .15em .35em; }
        pre { padding: 1em; overflow: auto; line-height: 1.45; }
        pre code { padding: 0; background: none; }
        table { border-collapse: collapse; display: block; overflow: auto; max-width: 100%; }
        th, td { border: 1px solid var(--line); padding: .4em .8em; }
        th { background: var(--code); }
        blockquote { margin: 0; padding: 0 1em; color: var(--muted); border-left: .25em solid var(--line); }
        hr { border: 0; border-top: 1px solid var(--line); }
        img { max-width: 100%; }
        """;
}
