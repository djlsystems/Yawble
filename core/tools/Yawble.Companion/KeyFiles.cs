using System.Text;
using Yawble.Core.Companion;

namespace Yawble.Companion;

/// <summary>
/// This computer's keys, one user-only file per paired client name: &lt;home&gt;\clients\&lt;name&gt;.key. The companion home and
/// its clients folder are made user-only first (<see cref="UserOnlyFile.EnsureDirectory"/>, which refuses a populated folder
/// that is not the companion's: that refusal surfaces as <see cref="HomeException"/>). A key is never printed or logged.
/// </summary>
internal sealed class KeyFiles(string home)
{
    public string Home => home;

    public string Dir => ControlDiscovery.ClientsDir(home);

    public string PathOf(string name) => Path.Combine(Dir, name + ".key");

    /// <summary>As the core's client names: lowercase letters, digits and '-', 1 to 32 characters, not starting with '-'.</summary>
    public static bool ValidName(string? name)
    {
        if (name is null || name.Length is < 1 or > 32 || name[0] == '-') return false;
        foreach (var c in name)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        return true;
    }

    /// <summary>Makes the home and its clients folder (user-only) before anything is asked of the app.</summary>
    public void Prepare()
    {
        if (string.IsNullOrEmpty(home)) throw new HomeException("yawble-companion has no folder for its files: set YAWBLE_HOME to a new or empty folder");
        try
        {
            UserOnlyFile.EnsureDirectory(home);
            UserOnlyFile.EnsureDirectory(Dir);
        }
        catch (InvalidOperationException e)
        {
            throw new HomeException(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new HomeException($"yawble-companion cannot use the folder '{home}' for its files ({e.Message}); point YAWBLE_HOME at a folder you own");
        }
    }

    public void Save(string name, string key)
    {
        if (!ValidName(name)) throw new UsageException($"'{name}' is not a client name");
        Prepare();
        try
        {
            UserOnlyFile.WriteAllBytes(PathOf(name), Encoding.UTF8.GetBytes(key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HomeException($"yawble-companion could not write its key file in '{Dir}' ({e.Message})");
        }
    }

    /// <summary>The key saved for <paramref name="name"/>, or null when there is none.</summary>
    public string? Load(string name)
    {
        if (!ValidName(name) || string.IsNullOrEmpty(home)) return null;
        try
        {
            var text = File.ReadAllText(PathOf(name), Encoding.UTF8).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HomeException($"yawble-companion could not read its key file for '{name}' in '{Dir}' ({e.Message})");
        }
    }

    /// <summary>Every client name with a key file here, sorted.</summary>
    public IReadOnlyList<string> Names()
    {
        if (string.IsNullOrEmpty(home) || !Directory.Exists(Dir)) return [];
        try
        {
            return [.. Directory.EnumerateFiles(Dir, "*.key").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(ValidName).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HomeException($"yawble-companion could not list its key files in '{Dir}' ({e.Message})");
        }
    }

    /// <summary>The one client name paired here; null when there is none or more than one.</summary>
    public string? OnlyName() => Names() is [var only] ? only : null;

    public bool Delete(string name)
    {
        if (!ValidName(name) || string.IsNullOrEmpty(home)) return false;
        var path = PathOf(name);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HomeException($"yawble-companion could not delete its key file '{path}' ({e.Message})");
        }
    }
}
