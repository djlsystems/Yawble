using System.Text.RegularExpressions;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(Harness.Tests.CollectionOrder))]

namespace Harness.Tests;

/// <summary>
/// THE LONGEST COLLECTIONS START FIRST. The tests of one class run one after another and classes
/// run side by side, so a class that takes four minutes and starts late finishes alone, and the
/// whole suite waits for it: measured on 2026-10-07, the last four and a half minutes of a fourteen
/// and a half minute run had about one test in flight. Started first, the long classes run beside
/// the short ones instead.
///
/// The worker-process collection goes first of all (it runs one class at a time for about three
/// minutes), then <see cref="LongestFirst"/> in that order, then every other collection in the
/// default order. The list is the measured longest classes, slowest first (from a TRX report's
/// per-test durations summed per class); a name that no longer matches a class is passed over, so
/// a rename costs only time. Re-measure and reorder when a class grows past the shortest here.
/// </summary>
public sealed class CollectionOrder : ITestCollectionOrderer
{
    internal static readonly string[] LongestFirst =
    [
        "ConnectionsTests",
        "LocalReposTests",
        "TeamArchiveTests",
        "CappedTriggerSleepTests",
        "TriggerCostControlTests",
        "WorktreePerCardAcceptanceTests",
        "TeamResetRepositoriesTests",
        "AbandonedTeamCreateTests",
        "PluginMemberEndToEndTests",
        "PullRequestTests",
        "ConnectionNeedsTests",
        "SettingsAuditTransactionTests",
        "LandedSurvivesCleanupTests",
        "PluginHostProbes",
        "ForeignToolsCheckTests",
        "PluginMemberRunsTests",
        "ConciergeMergeTests",
        "AgentInstallControlRouteTests",
        "LedgerSurvivesTests",
        "ConciergeArchiveTests",
        "PluginQuietRunTests",
        "RepoDefaultBranchTests",
        "ContributorModeTests",
        "DefaultBranchMovedTests",
        "PluginInstallRouteTests",
        "ImapConnectionsTests",
    ];

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
    {
        var ordered = DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections);

        return [.. ordered.OrderBy(c => Rank(c))];
    }

    /// <summary>0 for the worker processes, 1 + the list position for a long class, then the rest.
    /// OrderBy is stable, so collections of one rank keep the default order.</summary>
    private static int Rank(ITestCollection collection)
    {
        var name = collection.TestCollectionDisplayName;

        if (name == "worker processes") return 0;

        for (var i = 0; i < LongestFirst.Length; i++)
            if (Regex.IsMatch(name, $@"\b{LongestFirst[i]}\b"))
                return 1 + i;

        return 1 + LongestFirst.Length;
    }
}
