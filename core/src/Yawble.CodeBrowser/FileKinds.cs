using System.Text;

namespace Yawble.CodeBrowser;

/// <summary>What a file is shown as, and the text of a text file.</summary>
public static class FileKinds
{
    /// <summary>Over this, a file is not previewed ("tooLarge").</summary>
    public const long MaxPreviewBytes = 1024 * 1024;

    /// <summary>Over this many lines, a text file is not previewed ("tooLarge").</summary>
    public const int MaxPreviewLines = 20_000;

    /// <summary>Over this, a file is not downloaded.</summary>
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    private static readonly Dictionary<string, string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
    };

    private static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };
    private static readonly HashSet<string> Json = new(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonc", ".json5", ".webmanifest" };
    private static readonly HashSet<string> Yaml = new(StringComparer.OrdinalIgnoreCase) { ".yaml", ".yml" };
    private static readonly HashSet<string> Xml = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".slnx", ".xaml", ".resx", ".nuspec", ".plist", ".xsd", ".xsl", ".xslt", ".manifest",
    };

    /// <summary>highlight.js language names, by extension (or whole file name).</summary>
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".fs"] = "fsharp", [".vb"] = "vbnet",
        [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".vue"] = "xml", [".html"] = "xml", [".htm"] = "xml", [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".py"] = "python", [".rb"] = "ruby", [".go"] = "go", [".rs"] = "rust", [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin",
        [".swift"] = "swift", [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".cc"] = "cpp", [".hpp"] = "cpp", [".cxx"] = "cpp",
        [".php"] = "php", [".sh"] = "bash", [".bash"] = "bash", [".zsh"] = "bash", [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
        [".sql"] = "sql", [".toml"] = "ini", [".ini"] = "ini", [".cfg"] = "ini", [".editorconfig"] = "ini", [".gitattributes"] = "ini",
        [".bat"] = "dos", [".cmd"] = "dos", [".lua"] = "lua", [".r"] = "r", [".dart"] = "dart", [".scala"] = "scala",
        [".diff"] = "diff", [".patch"] = "diff", [".graphql"] = "graphql", [".proto"] = "protobuf",
        ["Dockerfile"] = "dockerfile", ["Makefile"] = "makefile", [".dockerfile"] = "dockerfile", [".mk"] = "makefile",
        [".json"] = "json", [".jsonc"] = "json", [".json5"] = "json", [".webmanifest"] = "json",
        [".yaml"] = "yaml", [".yml"] = "yaml", [".md"] = "markdown", [".markdown"] = "markdown",
    };

    public static string? ImageMime(string name) => Images.GetValueOrDefault(Path.GetExtension(name));

    /// <summary>"text" | "markdown" | "image" | "json" | "yaml" | "xml", by name only.</summary>
    public static string TypeOf(string name)
    {
        var ext = Path.GetExtension(name);
        if (Images.ContainsKey(ext)) return "image";
        if (Markdown.Contains(ext)) return "markdown";
        if (Json.Contains(ext)) return "json";
        if (Yaml.Contains(ext)) return "yaml";
        if (Xml.Contains(ext)) return "xml";
        return "text";
    }

    public static string? LanguageOf(string name)
    {
        var file = Path.GetFileName(name);
        if (Languages.TryGetValue(file, out var byName)) return byName;
        if (file.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        var ext = Path.GetExtension(file);
        if (Xml.Contains(ext)) return "xml";
        return ext.Length > 0 ? Languages.GetValueOrDefault(ext) : null;
    }

    /// <summary>Whether bytes look binary: a NUL in the first 8 KB, unless the text starts with a UTF-16 byte-order mark.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)) return false;
        var head = bytes[..Math.Min(bytes.Length, 8000)];
        return head.IndexOf((byte)0) >= 0;
    }

    /// <summary>The text of bytes: UTF-8 (byte-order mark dropped), or UTF-16 when it starts with that mark. Invalid bytes become U+FFFD.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>A text's lines, as an editor numbers them: split on \n, \r\n or \r; a final line break adds no line.</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The number of lines without splitting (for the 20,000-line check).</summary>
    public static int CountLines(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return 0;
        var n = bytes.Count((byte)'\n');
        return bytes[^1] == (byte)'\n' ? n : n + 1;
    }

    /// <summary>
    /// Reads a file for preview or diffing: its bytes when it is at most <see cref="MaxPreviewBytes"/>, with whether it is
    /// binary. Null bytes when it is larger (then only the size is known).
    /// </summary>
    internal static (byte[]? Bytes, long Size) ReadBounded(FileStream stream, long max = MaxPreviewBytes)
    {
        var size = stream.Length;
        if (size > max) return (null, size);
        using var ms = new MemoryStream((int)Math.Min(size, max) + 16);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) return (null, ms.Length); // grew while reading
        }
        return (ms.ToArray(), ms.Length);
    }
}
