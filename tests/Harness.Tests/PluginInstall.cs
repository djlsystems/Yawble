using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// Installs a test plugin under a data root's <c>plugins</c> directory, the way an operator does:
/// <c>plugins/&lt;id&gt;/&lt;version&gt;/plugin.json</c>, an executable script, and <c>active</c>.
/// </summary>
internal static class PluginInstall
{
    public static string PluginsRoot(string dataRoot) => Path.Combine(dataRoot, "plugins");

    /// <summary>A valid v1 manifest for <paramref name="id"/>; <paramref name="edit"/> changes it.</summary>
    public static JsonObject Manifest(string id = "sample-echo", string version = "0.1.0", Action<JsonObject>? edit = null)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["name"] = "Test " + id,
            ["description"] = "A test plugin.",
            ["version"] = version,
            ["protocol"] = PluginManifest.ProtocolV1,
            ["executable"] = new JsonObject { ["path"] = "bin/run", ["args"] = new JsonArray() },
        };

        edit?.Invoke(manifest);
        return manifest;
    }

    /// <summary>Writes the plugin and returns its version directory.</summary>
    public static string Write(
        string dataRoot, string id = "sample-echo", string version = "0.1.0",
        string script = "cat >/dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"ok\"}'",
        JsonObject? manifest = null, bool active = true)
    {
        var directory = Path.Combine(PluginsRoot(dataRoot), id, version);
        Directory.CreateDirectory(Path.Combine(directory, "bin"));

        File.WriteAllText(
            Path.Combine(directory, PluginManifest.FileName),
            (manifest ?? Manifest(id, version)).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var executable = Path.Combine(directory, "bin", "run");
        File.WriteAllText(executable, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        if (active) File.WriteAllText(Path.Combine(PluginsRoot(dataRoot), id, PluginCatalog.ActiveFile), version + "\n");

        return directory;
    }
}
