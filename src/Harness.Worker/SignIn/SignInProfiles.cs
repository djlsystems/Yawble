using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// THE SIGN-IN BROWSERS' PROFILES ON THE VOLUME: one per plugin member's table row, at
/// <c>&lt;root&gt;/&lt;member key&gt;/&lt;row key&gt;/</c>, reused on every sign-in for that row so the site sees
/// the same browser come back. The keys are hashes control makes (<c>SignInProfileKey</c>): nothing on
/// the volume names a member or a row. Every folder is 0700 and the worker user's: a profile holds a
/// signed-in session, and no agent can read it. A profile is used by one browser at a time
/// (<see cref="Lock"/>), and is removed with its row or its member (<see cref="Delete"/>).
/// </summary>
/// <remarks>
/// Inside a row's folder: <c>chrome</c> (the user data directory), <c>home</c> (the browser's HOME:
/// its certificate store and caches) and <c>run</c> (the display's cookie, removed when the browser
/// closes).
/// </remarks>
public static partial class SignInProfiles
{
    /// <summary>The volume folder for profiles when nothing says otherwise.</summary>
    public const string DefaultFolder = "sign-in-profiles";

    /// <summary>
    /// Where the profiles are: <c>HARNESS_SIGN_IN_PROFILES</c>, else <c>sign-in-profiles</c> in the data
    /// root when that is there to write in (the volume, prepared for the worker user), else
    /// <paramref name="fallback"/> - the worker's own state folder, which does not outlive its container.
    /// </summary>
    public static string Root(string fallback)
    {
        if (Environment.GetEnvironmentVariable("HARNESS_SIGN_IN_PROFILES") is { Length: > 0 } set) return set;

        var data = Environment.GetEnvironmentVariable("HARNESS_DATA_ROOT") is { Length: > 0 } root ? root : "/data";
        var volume = Path.Combine(data, DefaultFolder);
        try
        {
            if (Directory.Exists(volume))
            {
                var probe = Path.Combine(volume, ".probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return volume;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not this user's to write (a worker run as another user than the volume was prepared for).
        }

        return Path.Combine(fallback, "profiles");
    }

    /// <summary>True for a row's key (<c>&lt;member&gt;/&lt;row&gt;</c>).</summary>
    public static bool IsKey(string key) => RowKey().IsMatch(key);

    /// <summary>True for a member's key or a row's.</summary>
    public static bool IsMemberOrRowKey(string key) => RowKey().IsMatch(key) || MemberKey().IsMatch(key);

    /// <summary>The folder of a member's or a row's key.</summary>
    public static string Folder(string root, string key) =>
        IsMemberOrRowKey(key) ? Path.Combine([root, .. key.Split('/')]) : throw new ArgumentException("Not a sign-in profile's key.", nameof(key));

    /// <summary>Creates <paramref name="folder"/> if it is missing, and makes it the worker's alone (0700).</summary>
    public static void MakePrivate(string folder)
    {
        var made = Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) made.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    }

    /// <summary>
    /// Takes the row's profile for one browser: an exclusive lock on <c>&lt;row&gt;.lock</c> beside it,
    /// held until the stream is disposed. Throws <see cref="IOException"/> while another browser has it.
    /// </summary>
    public static FileStream Lock(string folder) =>
        new(folder + ".lock", new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    /// <summary>
    /// Readies a user data directory for a start: the person's languages (what the site reads as
    /// <c>navigator.languages</c> and <c>Accept-Language</c>) in Chrome's own preference, the last exit
    /// marked clean (no "restore pages?" bubble after a stop), and the single-instance lock of a
    /// browser stopped on another worker gone - <see cref="Lock"/> already says nobody else has it.
    /// </summary>
    public static void Prepare(string data, IReadOnlyList<string> languages)
    {
        foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
        {
            var path = Path.Combine(data, name);
            if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
        }

        var profile = Path.Combine(data, "Default");
        MakePrivate(profile);
        var file = Path.Combine(profile, "Preferences");
        JsonObject preferences;
        try
        {
            preferences = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject read ? read : [];
        }
        catch (JsonException)
        {
            preferences = [];
        }

        var intl = preferences["intl"] as JsonObject ?? [];
        intl["accept_languages"] = string.Join(',', languages);
        intl["selected_languages"] = string.Join(',', languages);
        preferences["intl"] = intl;
        var state = preferences["profile"] as JsonObject ?? [];
        state["exit_type"] = "Normal";
        state["exited_cleanly"] = true;
        preferences["profile"] = state;

        using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new Utf8JsonWriter(stream);
        preferences.WriteTo(writer);
    }

    /// <summary>Removes the profiles of a member's or a row's key, and a row's lock file. Nothing there is nothing to do.</summary>
    public static void Delete(string root, string key)
    {
        var folder = Folder(root, key);
        Remove(folder);
        try
        {
            File.Delete(folder + ".lock");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes one folder of a sign-in browser's, quietly when it is already gone.</summary>
    public static void Remove(string folder)
    {
        if (!Directory.Exists(folder)) return;

        try
        {
            // RECURSIVE DELETE REVIEWED: agents cannot write here. A sign-in browser's profile or run folder: the worker makes every level 0700 as itself, in the worker's own sign-in-profiles folder (worker:worker 0700 on the volume) or its state folder.
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}/[0-9a-f]{32}$")]
    private static partial Regex RowKey();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex MemberKey();
}
