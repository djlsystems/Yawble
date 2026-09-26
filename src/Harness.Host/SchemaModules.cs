using Harness.Backlog;
using Harness.Contracts;
using Harness.Identity;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

/// <summary>
/// Every schema module's steps, concatenated in ONE place and in the order Program.cs applies
/// them: messages, then auth, then skills, then backlog. Both callers - Program.cs on every boot,
/// OperatorCommands.cs for its recovery switches - read this list, because a second copy falls
/// behind when a module is added: <c>--reset-password</c> would then hand
/// <see cref="SchemaMigrator.ApplyAsync"/> a list missing that module's steps against a database
/// that already has them. ApplyAsync cannot tell a partial list from a newer build - see its own
/// remarks - so it would refuse every instance a live host had ever booted, which is exactly the
/// instance an operator reaches for this command on.
///
/// A NEW schema module belongs in this list the moment it exists, or every operator switch built on
/// <see cref="SchemaMigrator.ApplyAsync"/> refuses that way for that module.
/// <c>SchemaModuleCoverageTests</c> catches a module added here without it, by reflecting over
/// what Harness.Host references for any type exposing a static <c>Steps</c> collection.
/// </summary>
public static class SchemaModules
{
    public static IReadOnlyList<MigrationStep> All { get; } =
        [.. MessageSchema.Steps, .. AuthSchema.Steps, .. SkillSchema.Steps, .. BacklogSchema.Steps];
}
