using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;

namespace Harness.Host;

/// <summary>
/// WHAT A TEAM'S MCP SERVER'S TOOL IS CALLED ON THE HARNESS SERVER (MCP 2): <c>&lt;server&gt;_&lt;tool&gt;</c>,
/// keeping only <c>[a-zA-Z0-9_-]</c>, and short enough that a CLI's own <c>mcp__harness__</c> prefix still
/// fits 64 characters. A name cut to fit ends in <c>_</c> and a short hash of the whole name, so two long
/// names that share their start stay apart.
/// </summary>
public static class McpToolNames
{
    /// <summary>The prefix a CLI puts before the harness server's tools, counted in the limit.</summary>
    public const string CliPrefix = "mcp__harness__";

    /// <summary>The longest name a CLI accepts, its prefix included.</summary>
    public const int Limit = 64;

    /// <summary>The longest name listed here: the limit less the prefix.</summary>
    public const int Longest = Limit - 14;

    private const int HashLength = 8;

    /// <summary>The platform's own tools' names: a server's tool named the same is never listed, so it cannot
    /// shadow one or be listed beside it.</summary>
    public static readonly IReadOnlySet<string> Platform = typeof(PlatformMcpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    public static string For(string server, string tool)
    {
        var whole = server + "_" + tool;
        var kept = new string([.. whole.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')]);
        if (kept.Length <= Longest) return kept;

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(whole)))[..HashLength];
        return kept[..(Longest - HashLength - 1)] + "_" + hash;
    }

    /// <summary>Every pair of tools that would get the same name, the first one listed first.</summary>
    public static IEnumerable<(string Name, (string Server, string Tool) First, (string Server, string Tool) Second)> Clashes(
        IEnumerable<(string Server, string Tool)> tools)
    {
        var seen = new Dictionary<string, (string Server, string Tool)>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = For(tool.Server, tool.Tool);
            if (seen.TryGetValue(name, out var first))
            {
                if (first != tool) yield return (name, first, tool);
                continue;
            }

            seen[name] = tool;
        }
    }
}
