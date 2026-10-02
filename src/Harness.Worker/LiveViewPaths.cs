using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Where a run's live view is, from the preset's view as the run protocol carries it: the
/// transcript path named before launch, or why the view is not one this platform reads. The
/// catalog's <c>LiveView.Resolve</c> and <c>LiveView.Refusal</c> answer through these.
/// </summary>
public static class LiveViewPaths
{
    /// <summary>
    /// The transcript path for one run, or null when <paramref name="view"/> is not one this
    /// platform will read, or is found after launch rather than named.
    /// </summary>
    public static string? Resolve(RunLiveView view, string home, string workingDirectory, string sessionId)
    {
        if (Refusal(view) is not null || string.IsNullOrEmpty(home) || view.Path is not { } path) return null;

        var relative = path[2..]
            .Replace("{sessionId}", sessionId, StringComparison.Ordinal)
            .Replace("{workspaceDashed}", LiveViewNames.Dashed(workingDirectory), StringComparison.Ordinal)
            .Replace("{workspaceEncoded}", LiveViewNames.Encoded(workingDirectory), StringComparison.Ordinal);

        return Path.Combine(home, relative);
    }

    /// <summary>Why a live view is refused, or null when it is usable.</summary>
    public static string? Refusal(RunLiveView view)
    {
        if (!LiveViewNames.Formats.Contains(view.Format, StringComparer.Ordinal))
        {
            return $"its live view format '{view.Format}' is not one this platform reads ({string.Join(", ", LiveViewNames.Formats)})";
        }

        if ((view.Path is null) == (view.Find is null))
        {
            return "its live view must name exactly one of path and find";
        }

        if (view.Path is { } path && !LiveViewNames.UnderHome(path))
        {
            return "its live view path must start with ~/ and may not contain ..";
        }

        if (view.Find is { } find)
        {
            if (!LiveViewNames.UnderHome(find.Folder))
            {
                return "its live view find folder must start with ~/ and may not contain ..";
            }

            if (string.IsNullOrWhiteSpace(find.Pattern) || find.Pattern.StartsWith('/')
                || find.Pattern.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                return "its live view find pattern must be relative, one name per level, with no . or ..";
            }

            if (!LiveViewNames.CwdRules.Contains(find.CwdFrom, StringComparer.Ordinal))
            {
                return $"its live view find cwdFrom '{find.CwdFrom}' is not one of {string.Join(", ", LiveViewNames.CwdRules)}";
            }
        }

        return null;
    }
}
