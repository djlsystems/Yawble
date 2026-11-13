using System.Diagnostics;

namespace Yawble.Core.Tests;

/// <summary>For every test that starts the real core process.</summary>
internal static class CoreLaunch
{
    /// <summary>
    /// Points the core's yawble companion home (YAWBLE_HOME) at a fresh temp folder, so the core's control.json is never
    /// written to the real %APPDATA%\yawble-companion. Returns the home (not created; the core makes it). A test that sets its
    /// own YAWBLE_HOME keeps it.
    /// </summary>
    public static string IsolateCompanionHome(ProcessStartInfo psi)
    {
        if (psi.Environment.TryGetValue(global::Yawble.Core.Companion.ControlDiscovery.HomeVariable, out var set) && !string.IsNullOrEmpty(set)) return set;
        var root = Directory.CreateTempSubdirectory("yawble-yhome-").FullName;
        lock (Made)
        {
            if (Made.Count == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
            Made.Add(root);
        }
        var home = Path.Combine(root, "home");
        psi.Environment[global::Yawble.Core.Companion.ControlDiscovery.HomeVariable] = home;
        return home;
    }

    /// <summary>The temp homes made here, removed when the test run ends (each core has exited by then).</summary>
    private static readonly List<string> Made = [];

    private static void RemoveAll()
    {
        string[] all;
        lock (Made) { all = [.. Made]; Made.Clear(); }
        foreach (var dir in all)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // still in use: the temp folder keeps it
        }
    }
}
