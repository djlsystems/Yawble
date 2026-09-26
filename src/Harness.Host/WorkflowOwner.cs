using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHICH CONTAINER A WORKFLOW WAS ADDRESSED TO - the authority behind `workflow-complete`.
///
/// A workflow's correlation IS the seq of its root message, and an addressed instruction carries its
/// addressee in its TYPE (`container.instruction.&lt;Team&gt;/&lt;Name&gt;`). So ownership is
/// DERIVED and needs no column: nothing to write, nothing to keep in step, and nothing that can be
/// wrong about a workflow that started before this code existed.
///
/// NULL IS "NOBODY WAS ADDRESSED", not an error. A workflow can be rooted in a schedule firing or a
/// kanban card event, and treating those as unowned would make them undeclarable - a team with an
/// open workflow it is structurally incapable of closing is exactly the defect this milestone exists
/// to remove. The route reads null as "the Manager owns it", which is the only thing derivable and
/// is what the platform did for every workflow until now.
///
/// Public rather than internal: there is no `InternalsVisibleTo` from `Harness.Host` to
/// `Harness.Host.Tests`, so an internal type here would be invisible to `WorkflowOwnerTests` and
/// the test project would not compile. It is a derivation over the log with no state, and there is
/// nothing to hide - the same reasoning that keeps `MemberBaseAddress` public in this namespace.
/// </summary>
public static class WorkflowOwner
{
    public static async Task<ContainerId?> OfAsync(
        IMessageLog log, long correlation, CancellationToken ct = default)
    {
        if (correlation <= 0) return null;

        if (await log.FindAsync(correlation, ct) is not { } root) return null;

        if (!root.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var addressed = root.Type[MessageTypes.InstructionPrefix.Length..];

        try
        {
            return ContainerId.Parse(addressed);
        }
        catch (FormatException)
        {
            // A ROW, NOT AN ARGUMENT. This text came off the log, which is append-only and outlives
            // every build that ever wrote to it, so a shape an older binary produced must degrade to
            // "no named owner" rather than take a live route down. The refusal belongs at the WRITE,
            // where a caller can be told; there is nobody to tell here.
            return null;
        }
    }
}
