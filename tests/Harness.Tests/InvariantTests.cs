using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

public sealed class UsageBillableTests
{
    [Fact]
    public void Cache_reads_are_not_full_price_input()
    {
        // 100 uncached + 3250 cache reads + 40 output. Full weight would be 3390.
        // Cache reads bill at 1/10: 100 + 325 + 40 = 465.
        var usage = new InvocationUsage(100, 40, "claude", cachedIn: 3250);

        Assert.Equal(465, usage.BillableTokens());
    }

    [Fact]
    public void Cache_writes_bill_at_five_quarters()
    {
        var usage = new InvocationUsage(0, 0, "claude", cacheCreation: 8);

        Assert.Equal(10, usage.BillableTokens());
    }

    [Fact]
    public void A_combined_total_is_not_reweighted()
    {
        var usage = InvocationUsage.Combined(900, "codex");

        Assert.Equal(900, usage.BillableTokens());
    }

    [Fact]
    public void Cached_input_may_exceed_uncached_input()
    {
        var usage = new InvocationUsage(1, 1, "claude", cachedIn: 50_000);

        Assert.Equal(1 + 1 + 5_000, usage.BillableTokens());
    }
}

public sealed class WipLedgerTests
{
    [Fact]
    public void The_cap_holds_work_and_names_who_has_the_slots()
    {
        var ledger = new WipLedger(1);
        var first = new ContainerId("alpha", "manager");
        var second = new ContainerId("beta", "dev1");

        var held = ledger.TryEnter(first);
        Assert.NotNull(held);
        Assert.Null(ledger.TryEnter(second));

        var view = ledger.View();
        Assert.Equal("alpha", view.Running[0].Team);
        Assert.Equal("manager", view.Running[0].Member);
        Assert.Equal("beta", view.Waiting[0].Team);
        Assert.Equal("dev1", view.Waiting[0].Member);

        held!.Dispose();
        Assert.NotNull(ledger.TryEnter(second));
        Assert.Empty(ledger.View().Waiting);
    }

    [Fact]
    public void Zero_is_unlimited()
    {
        var ledger = new WipLedger(0);
        Assert.NotNull(ledger.TryEnter(new ContainerId("alpha", "a")));
        Assert.NotNull(ledger.TryEnter(new ContainerId("beta", "b")));
    }
}

public sealed class IdentityTests
{
    [Fact]
    public void Cookie_name_is_stable_across_calls()
    {
        var first = InstanceIdentity.CookieNameFor("/data");
        var second = InstanceIdentity.CookieNameFor("/data");

        Assert.Equal(first, second);
        Assert.StartsWith("harness-", first);
    }
}

public sealed class McpContractTests
{
    [Fact]
    public void Built_in_skills_send_agents_to_mcp_tools_not_the_http_api()
    {
        // CLI-style `harness <verb>` commands. A skill that shows one is obeyed literally by an
        // agent that has no such shell command to run, so the text must describe the MCP tool instead.
        var cliVerb = new System.Text.RegularExpressions.Regex(
            @"\bharness (tell|status|member|hiring|kanban|backlog|repo|progress|blocked|handback|needs-decision|workflow-complete|skills|workflow)\b");

        // The built-in PROMPTS are obeyed the same way, so they are held to the same ban.
        foreach (var (_, prompt) in BuiltInPrompts.All)
        {
            Assert.DoesNotMatch(cliVerb, prompt);
            Assert.DoesNotContain("curl ", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("--team", prompt, StringComparison.Ordinal);
        }

        foreach (var body in BuiltInSkills.Bodies())
        {
            Assert.Contains("`member`", body, StringComparison.Ordinal);
            Assert.Contains("`hiring`", body, StringComparison.Ordinal);
            Assert.Contains("`status`", body, StringComparison.Ordinal);
            Assert.Contains("`kanban`", body, StringComparison.Ordinal);
            Assert.Contains("`backlog`", body, StringComparison.Ordinal);
            Assert.Contains("`repo`", body, StringComparison.Ordinal);
            Assert.Contains("`skills_get`", body, StringComparison.Ordinal);
            Assert.Contains("`skills_search`", body, StringComparison.Ordinal);
            Assert.Contains("`tell`", body, StringComparison.Ordinal);

            Assert.DoesNotMatch(cliVerb, body);
            Assert.DoesNotContain("curl ", body, StringComparison.Ordinal);
            Assert.DoesNotContain("wget ", body, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/", body, StringComparison.Ordinal);
        }
    }
}

public sealed class BrandLeakTests
{
    [Fact]
    public void Brand_strings_stay_out_of_the_code()
    {
        var root = FindRepoRoot();
        // The product name, and the only two files under code that may carry it. Renaming the
        // product edits these and nothing else; anything that names it elsewhere is an
        // identifier-bearing value that would need a migration. Split so this file does not match.
        var brand = "yaw" + "ble";
        var presentation = new[] { "web/src/presentation/product.ts", "web/package.json" };

        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var text = File.ReadAllText(file);

            if (text.Contains(brand, StringComparison.OrdinalIgnoreCase)
                && !presentation.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                hits.Add(relative + " names the brand outside the presentation layer");
            }
        }

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void The_brand_lives_in_the_presentation_layer()
    {
        var root = FindRepoRoot();
        var product = File.ReadAllText(Path.Combine(root, "web", "src", "presentation", "product.ts"));

        Assert.Contains("productTitle = 'Yaw" + "ble'", product, StringComparison.Ordinal);
    }

    private static bool Skip(string file)
    {
        var path = file.Replace('\\', '/');
        if (path.Contains("/bin/") || path.Contains("/obj/") || path.Contains("/node_modules/")) return true;
        if (path.Contains("/docs/")) return true;
        // The operator CLI is the product's own binary and is named after the brand by design.
        if (path.Contains("/cli/")) return true;
        // Another checkout of this repo (a Claude Code worktree), checked by its own run.
        if (path.Contains("/.claude/worktrees/")) return true;
        if (path.Contains("/web/dist/") || path.Contains("/wwwroot/")) return true;

        var extension = Path.GetExtension(file);
        return extension is not (".cs" or ".ts" or ".vue" or ".json" or ".scss" or ".html" or ".csproj" or ".props");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}

