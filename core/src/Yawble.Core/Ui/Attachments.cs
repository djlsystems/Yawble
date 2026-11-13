using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Ui;

/// <summary>
/// Images the person attaches to a message (pasted or picked): saved where the agent that reads the message can read
/// them, and named in the message with their paths, since an agent sees an image by opening its file. The Concierge's
/// go to its conversation's own home folder; a team's to the team's documents (attachments/), which every member can
/// read and the person sees in Documents.
/// </summary>
public static class Attachments
{
    public const int MaxCount = 6;
    public const int MaxBytes = 10 * 1024 * 1024;
    public const string Folder = "attachments";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "png", [".jpg"] = "jpg", [".jpeg"] = "jpg", [".gif"] = "gif", [".webp"] = "webp",
    };

    /// <summary>The images in a request's "images" ([{ name, base64 }]); none when it has none. Checked, not yet saved.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> Read(JsonObject? p)
    {
        if (p?["images"] is not JsonArray list || list.Count == 0) return [];
        if (list.Count > MaxCount) throw new RpcException("bad_params", $"attach at most {MaxCount} images to one message");
        var images = new List<(string, byte[])>();
        foreach (var node in list)
        {
            var name = node?["name"]?.GetValue<string>() ?? "image.png";
            var base64 = node?["base64"]?.GetValue<string>() ?? throw new RpcException("bad_params", "an image has no data");
            var comma = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.Ordinal) && comma > 0) base64 = base64[(comma + 1)..];
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new RpcException("bad_params", $"{name} is not readable image data"); }
            if (bytes.Length > MaxBytes) throw new RpcException("bad_params", $"{name} is larger than {MaxBytes / 1024 / 1024} MB");
            var ext = Kind(bytes) ?? throw new RpcException("bad_params", $"{name} is not a PNG, JPEG, GIF or WebP image");
            images.Add((Clean(name, ext), bytes));
        }
        return images;
    }

    /// <summary>Saves the images in <paramref name="folder"/>/attachments, each under a new name. Returns their full paths.</summary>
    public static IReadOnlyList<string> Save(string folder, IReadOnlyList<(string Name, byte[] Bytes)> images, TimeProvider? clock = null)
    {
        if (images.Count == 0) return [];
        var dir = Path.Combine(folder, Folder);
        Directory.CreateDirectory(dir);
        var stamp = (clock ?? TimeProvider.System).GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var paths = new List<string>();
        foreach (var (name, bytes) in images)
        {
            var path = Path.Combine(dir, $"{stamp}-{name}");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stamp}-{n}-{name}");
            File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>The message with its images named: what the agent reads.</summary>
    public static string WithImages(string text, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return text;
        var sb = new StringBuilder(text.TrimEnd());
        sb.Append("\n\n").Append(paths.Count == 1 ? "The person attached an image" : $"The person attached {paths.Count} images")
          .Append(". Open each with your file-reading tool to see it:");
        foreach (var p in paths) sb.Append("\n- ").Append(p);
        return sb.ToString();
    }

    /// <summary>The image's real kind, from its first bytes (never trusted from its name).</summary>
    internal static string? Kind(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
        return null;
    }

    /// <summary>A safe file name: the given stem (letters, digits, '-', '_', '.'), with the extension of its real kind.</summary>
    internal static string Clean(string name, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(name ?? "");
        var sb = new StringBuilder();
        foreach (var c in stem) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        var clean = sb.ToString().Trim('-', '.');
        if (clean.Length == 0) clean = "image";
        if (clean.Length > 60) clean = clean[..60];
        return $"{clean}.{kind}";
    }
}
