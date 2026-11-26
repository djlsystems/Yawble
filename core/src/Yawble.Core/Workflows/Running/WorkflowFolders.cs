namespace Yawble.Core.Workflows.Running;

/// <summary>Where workflow members keep their files: &lt;data&gt;/workflows (members/&lt;id&gt;, test/&lt;id&gt;, dry-run/&lt;id&gt;).</summary>
public static class WorkflowFolders
{
    /// <summary>The data folder's subfolder for workflow files.</summary>
    public const string Name = "workflows";

    /// <summary>The subfolder's name before migration 0060, when workflows were called scripts.</summary>
    public const string OldName = "scripts";

    /// <summary>
    /// Moves &lt;data&gt;/scripts to &lt;data&gt;/workflows when only the old one exists. Nothing happens when the new one is
    /// already there (moved before, or made by this version), so the old folder is never merged over newer files.
    /// </summary>
    /// <returns>True when the folder was moved.</returns>
    public static bool MoveOldFolder(string dataDir)
    {
        var old = Path.Combine(dataDir, OldName);
        var current = Path.Combine(dataDir, Name);
        if (!Directory.Exists(old) || Directory.Exists(current)) return false;
        Directory.Move(old, current);
        return true;
    }
}
