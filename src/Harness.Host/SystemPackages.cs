using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// THE "SYSTEM PACKAGES" SETTING, HANDED TO THE ENTRYPOINT.
///
/// <para>
/// Agents are not root, so they cannot <c>apt-get install</c>; a person names the OS packages an
/// instance needs in Tenant Settings instead, and the container installs them as root at its next
/// start, before it drops to the host's user. The entrypoint runs before the host and does not read
/// the database, so the host keeps a plain copy of the list at <c>&lt;dataRoot&gt;/system-packages</c>:
/// one name per line, rewritten at every start and on every change. The table row stays the record;
/// this file is its hand-off.
/// </para>
///
/// <para>
/// A name is checked twice, here and by <c>scripts/prepare-volume.sh</c>, against the same rule: the
/// file is read by a root shell, and a hand-edited line must not reach <c>apt-get</c> as anything but
/// one package name.
/// </para>
/// </summary>
public static partial class SystemPackages
{
    /// <summary>The file the entrypoint reads, under the data root.</summary>
    public const string FileName = "system-packages";

    /// <summary>The sentence spec design 5 puts in front of a person.</summary>
    public const string RestartSentence = "Adding one costs a restart, not an image rebuild.";

    public const int MaxCount = 100;

    /// <summary>
    /// Debian policy's package name: lower-case letters, digits, <c>+</c>, <c>-</c> and <c>.</c>, at
    /// least two characters, starting with a letter or digit. No version, no architecture, no
    /// option: nothing here can be read by <c>apt-get</c> or a shell as more than a name.
    /// scripts/prepare-volume.sh holds the same pattern.
    /// </summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>Writes the list for the next start, owner-only, replacing the file in one rename.</summary>
    public static void WriteFile(string dataRoot, IReadOnlyList<string> packages)
    {
        var path = Path.Combine(dataRoot, FileName);
        var temporary = path + ".tmp";
        var text = "# Written by the host from the \"System packages\" setting. Edit it there, not here.\n"
            + string.Concat(packages.Select(package => package + "\n"));

        File.Delete(temporary);

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(temporary, text);
        }
        else
        {
            using var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(text);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
