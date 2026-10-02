using System.Text;

namespace Harness.Pty;

/// <summary>
/// Splits a shell-style command line into an executable file and its arguments.
/// The first whitespace-delimited token is the executable; the rest are arguments.
/// Double quotes group a run of characters (including spaces) into a single token.
/// </summary>
public static class CommandLineSplitter
{
    public static (string File, string[] Args) Split(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        return (tokens[0], tokens.Skip(1).ToArray());
    }

    /// <summary>
    /// Splits off only the executable, returning the rest of the command line VERBATIM -
    /// original spacing and quoting untouched.
    /// </summary>
    public static (string File, string Tail) SplitFirst(string commandLine)
    {
        var file = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        for (; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (file.Length > 0)
                {
                    break;
                }

                continue;
            }

            file.Append(c);
        }

        return (file.ToString(), commandLine[i..].TrimStart());
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
