using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>
/// Websites as integrations, the setup half: a website login is a Connection of kind "website" (the site's
/// address, no secret; the browser session lives only in the app window's own partition), and a Website is an <c>apis</c> row of kind
/// "website" with six fixed actions. The window on this computer does the browsing through <see cref="IWebsiteHost"/>.
/// </summary>
public sealed partial class IntegrationService
{
    /// <summary>The partition of a website login's session: the Connection's random store key, never its id.</summary>
    public const string WebsitePartitionPrefix = "persist:website-";

    private static bool IsWebsiteMethod(string method) => method is "integrations.websiteSignIn" or "integrations.websiteSignInDone";

    /// <summary>Where background website failures are reported (never page text or a session). Default: stderr; tests replace it.</summary>
    internal Action<string> WebsiteLog { get; set; } = line => Console.Error.WriteLine(line);

    private IWebsiteHost WebsiteHost => websites ?? throw new RpcException("unavailable", "websites are not available here");

    /// <summary>
    /// Whether <paramref name="url"/> is inside <paramref name="site"/>: for the site host H (lower case) and B = H without a leading "www.",
    /// the address is https (http only when the site itself is http), has no user info, and its host is B or ends with "." + B. Pure.
    /// </summary>
    public static bool WithinSite(Uri url, Uri site)
    {
        if (!url.IsAbsoluteUri || !site.IsAbsoluteUri || url.UserInfo.Length > 0) return false;
        var schemeOk = url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && site.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk) return false;
        var b = site.IdnHost.ToLowerInvariant();
        // "www." is dropped only when a dot remains ("www.com" must not cover all of .com)
        if (b.StartsWith("www.", StringComparison.Ordinal) && b[4..].Contains('.')) b = b[4..];
        var h = url.IdnHost.ToLowerInvariant();
        return b.Length > 0 && (h == b || h.EndsWith("." + b, StringComparison.Ordinal));
    }

    /// <summary>A site address as typed: http(s), a host, no user info. Kept as scheme + host + path (no query, no fragment).</summary>
    private static Uri SiteUrl(string? text, string what)
    {
        if (HttpUrl(text) is not { } u) throw new ArgumentException($"{what} must be a full web address starting with http:// or https://");
        if (u.UserInfo.Length > 0) throw new ArgumentException($"{what} must not contain a user name or password");
        return new Uri(u.GetLeftPart(UriPartial.Path));
    }

    /// <summary>A host request with its deadline: when the core stops waiting for it (the service clock + <see cref="ShellWebsiteHost.WebsiteTimeout"/>).</summary>
    private WebsiteRequest WebsiteRequestFor(string op, string partition, string pageKey, string siteUrl, JsonObject args) =>
        new(op, partition, pageKey, siteUrl, args, (_clock.GetUtcNow() + ShellWebsiteHost.WebsiteTimeout).ToUnixTimeMilliseconds());

    /// <summary>The session partition of a public Website (no login): by its row id.</summary>
    private static string PublicWebsitePartition(long apiId) => WebsitePartitionPrefix + "public-" + apiId;

    private string WebsitePartition(long connectionId) =>
        WebsitePartitionPrefix + (Conns.StoreKeyOf(connectionId) ?? throw new ArgumentException("no such connection"));

    /// <summary>The Connection as a website login: its kind is "website" and it has a site address.</summary>
    private (ConnectionView View, Uri Site) WebsiteLogin(long id)
    {
        var view = Conns.Get(id) ?? throw new ArgumentException("no such connection");
        if (view.Kind != "website") throw new ArgumentException($"'{view.Name}' is not a website login");
        var site = view.Fields.FirstOrDefault(f => f.Key == "site")?.Shown;
        return (view, SiteUrl(site, "the site"));
    }

    private async Task<JsonNode> WebsiteConnectionSaveAsync(JsonObject? p)
    {
        var id = OptLong(p, "id");
        var site = SiteUrl(OptStr(p, "site"), "the site");
        var before = id is { } eid ? Conns.Get(eid) : null;
        var name = OptStr(p, "name")?.Trim() ?? before?.Name; // an edit with no name keeps its name
        if (name is null)
        {
            var baseName = site.Host + " login";
            var taken = Conns.List().Where(c => c.Id != id).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            name = baseName;
            for (var i = 2; taken.Contains(name); i++) name = $"{baseName} {i}";
        }
        if (before is not null && before.Kind != "website") throw new ArgumentException("that connection is not a website login");
        var saved = Conns.Save(id, name, [new ConnectionField("site", "Site", false, site.AbsoluteUri)], new Dictionary<string, string>(), "website");
        // a new address is not the one the person signed in to
        if (before?.Fields.FirstOrDefault(f => f.Key == "site")?.Shown is { } old && old != site.AbsoluteUri)
        {
            Conns.MarkStatus(saved.Id, "untested", null);
            saved = Conns.Get(saved.Id)!;
            // another site: the old site's cookies have no business in this login's session
            if (!Uri.TryCreate(old, UriKind.Absolute, out var oldUri) || !string.Equals(oldUri.IdnHost, site.IdnHost, StringComparison.OrdinalIgnoreCase))
                await ForgetWebsiteAsync(WebsitePartition(saved.Id));
        }
        return ConnectionJson(saved);
    }

    private async Task<JsonNode> WebsiteSignInAsync(long id, CancellationToken ct)
    {
        var (_, site) = WebsiteLogin(id);
        await WebsiteSendAsync(WebsiteRequestFor("signIn", WebsitePartition(id), "signin:" + id, site.AbsoluteUri, new JsonObject { ["connectionId"] = id }), ct);
        return new JsonObject { ["ok"] = true };
    }

    private async Task<JsonNode> WebsiteSignInDoneAsync(long id, CancellationToken ct)
    {
        var (_, site) = WebsiteLogin(id);
        await WebsiteSendAsync(WebsiteRequestFor("signInClose", WebsitePartition(id), "signin:" + id, site.AbsoluteUri, new JsonObject { ["connectionId"] = id }), ct);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>One host request; a failed reply or an absent window is an RpcException the person can read.</summary>
    private async Task<WebsiteReply> WebsiteSendAsync(WebsiteRequest request, CancellationToken ct)
    {
        try
        {
            var reply = await WebsiteHost.RunAsync(request, ct);
            if (!reply.Ok) throw new RpcException("website", Clip(reply.Error ?? "the website window did not do that", 300));
            return reply;
        }
        catch (WebsiteException e)
        {
            throw new RpcException("website", e.Message);
        }
    }

    /// <summary>
    /// Called by the app window when the person pressed Done and the sign-in window is closed (hostBridge.signedIn). Returns at once with the
    /// background check, which never throws: the IPC connection handles one request at a time and the check's own reply comes back on it, so
    /// waiting here would deadlock.
    /// </summary>
    public Task WebsiteSignedIn(long connectionId) => StartWebsiteCheck(connectionId);

    /// <summary>The last background check started (tests wait on it).</summary>
    internal Task? LastWebsiteCheck { get; private set; }

    /// <summary>
    /// Runs <see cref="WebsiteCheckAsync"/> in the background and sets the status: works inside the site, needs reconnect outside it. When the
    /// window did not answer, nothing is proved: the login is untested with the reason, except that a "needs reconnect" stays until a check
    /// proves it works (connections.changed is raised either way, so a screen waiting on "checking..." moves on). Never throws; a failure is
    /// logged with no page text and no session.
    /// </summary>
    private Task StartWebsiteCheck(long connectionId)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var (status, reason) = await WebsiteCheckAsync(connectionId, cts.Token);
                if (status is not null) { Conns.MarkStatus(connectionId, status, reason); return; }
                WebsiteLog($"websites: the check of connection {connectionId} proved nothing ({Clip(reason, 200)})");
                if (Conns.Brief(connectionId) is not { } now) return;
                if (now.Status == "needs_reconnect") Conns.Changed?.Invoke();
                else Conns.MarkStatus(connectionId, "untested", reason);
            }
            catch (Exception e)
            {
                WebsiteLog($"websites: the check of connection {connectionId} failed ({e.GetType().Name}: {Clip(e.Message, 200)})");
                try { Conns.Changed?.Invoke(); } catch (Exception) { /* a screen refresh only */ }
            }
        });
        LastWebsiteCheck = task;
        return task;
    }

    /// <summary>
    /// Loads the site's address hidden in the login's own session. Ends inside the site: works. Ends outside it: needs reconnect, with the host
    /// it went to. The status to set is null when nothing was proved (the window did not answer): then only a reason is given.
    /// </summary>
    private async Task<(string? Status, string Reason)> WebsiteCheckAsync(long id, CancellationToken ct)
    {
        var (_, site) = WebsiteLogin(id);
        WebsiteReply reply;
        try
        {
            reply = await WebsiteHost.RunAsync(WebsiteRequestFor("check", WebsitePartition(id), "check:" + id, site.AbsoluteUri, new JsonObject()), ct);
        }
        catch (Exception e) when (e is WebsiteException or RpcException)
        {
            return (null, $"could not check the site: {e.Message}");
        }
        if (!reply.Ok) return (null, $"could not check the site: {Clip(reply.Error ?? "no answer", 200)}");
        if (reply.StoppedAt is null && reply.Url is null) return (null, "could not check the site: the window did not say where the page ended");
        var ended = Uri.TryCreate(reply.StoppedAt ?? reply.Url, UriKind.Absolute, out var u) ? u : null;
        if (reply.StoppedAt is null && ended is not null && WithinSite(ended, site))
            return ("works", $"opened {site.Host} signed in at {Stamp()}");
        var host = ended?.Host ?? reply.StoppedAt ?? "another address";
        return ("needs_reconnect", $"the site sent the page to {host}, outside {site.Host}, at {Stamp()}: sign in again");
    }

    /// <summary>
    /// Test on a website login: the check runs in the background (a page load can take most of a minute, and this RPC holds the window's main
    /// connection), so this returns at once with checking: true; the result comes with connections.changed.
    /// </summary>
    private JsonNode WebsiteConnectionTest(long id)
    {
        var view = Conns.Get(id) ?? throw new ArgumentException("no such connection");
        WebsiteLogin(id); // a website login with a site address, or a sentence
        _ = StartWebsiteCheck(id);
        return new JsonObject { ["status"] = view.Status, ["reason"] = "checking the site", ["changed"] = false, ["checking"] = true };
    }

    /// <summary>
    /// Asks the window to clear a removed login's session and close its pages. Best effort: a failure is logged, and the removal stands
    /// (the partition name is random and unreachable once its Connection is gone).
    /// </summary>
    private async Task ForgetWebsiteAsync(string partition, string? pagesOf = null)
    {
        if (websites is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var args = pagesOf is null ? new JsonObject() : new JsonObject { ["pagesOf"] = pagesOf };
            await websites.RunAsync(WebsiteRequestFor("forget", partition, "", "", args), cts.Token);
        }
        catch (Exception e)
        {
            WebsiteLog($"websites: a removed login's session could not be cleared now ({e.GetType().Name}: {Clip(e.Message, 200)})");
        }
    }

    /// <summary>
    /// Saves a Website: the site address, and the website login it uses (none = a public site). Its actions are the fixed six, written by
    /// Discover. A name that belongs to a web API is refused, and the other way round.
    /// </summary>
    private JsonNode WebsiteSave(JsonObject? p)
    {
        var given = OptStr(p, "name")?.Trim();
        var existing = given is { Length: > 0 } ? ApiRows(given).FirstOrDefault() : null;
        if (existing is { Kind: not "website" }) throw new ArgumentException($"{existing.Name} is a web API, not a website");
        var urlText = OptStr(p, "url") ?? OptStr(p, "baseUrl");
        if (urlText is null && existing is null) throw new ArgumentException("give the site's address");
        var site = urlText is null ? SiteUrl(existing!.BaseUrl, "the site") : SiteUrl(urlText, "the site");
        var name = given is { Length: > 0 } ? given : site.Host;
        existing ??= ApiRows(name).FirstOrDefault();
        if (existing is { Kind: not "website" }) throw new ArgumentException($"{existing.Name} is a web API, not a website");
        if (name.Length > 60) throw new ArgumentException("the name is too long (60 characters at most)");
        if (existing is not null && p?["create"] is JsonValue cr && cr.TryGetValue<bool>(out var create) && create)
            throw new ArgumentException($"a website named {existing.Name} already exists; edit it instead");

        var tool = ToolName(name);
        if ((WebsiteToolPrefix + tool).Contains("__", StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' would give agents the tool name '{WebsiteToolPrefix}{tool}', and two underscores in a row are kept for MCP servers' tools: choose another name");
        if (ApiRows().FirstOrDefault(a => a.Kind == "website" && !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) && ToolName(a.Name) == tool) is { } clash)
            throw new ArgumentException($"'{name}' is too close to the existing website '{clash.Name}' (agents would see the same tool name '{WebsiteToolPrefix}{tool}'): choose another name");
        if (Rows().FirstOrDefault(r => ToolName(r.Name) == WebsiteToolPrefix + tool) is { } cli)
            throw new ArgumentException($"'{name}' would give agents the tool name '{WebsiteToolPrefix}{tool}', which the CLI integration '{cli.Name}' already has: choose another name");

        long? connectionId = existing?.ConnectionId;
        if (p is not null && p.ContainsKey("connectionId"))
        {
            connectionId = OptLong(p, "connectionId");
            if (connectionId is { } cid)
            {
                var login = Conns.Get(cid) ?? throw new ArgumentException("no such connection");
                if (login.Kind != "website") throw new ArgumentException($"'{login.Name}' is not a website login: pick a website login, or none for a public site");
            }
        }

        var now = Now();
        if (existing is null)
            Exec("INSERT INTO apis (name, kind, base_url, connection_id, created_at, updated_at) VALUES ($n, 'website', $b, $c, $now, $now);",
                ("$n", name), ("$b", site.AbsoluteUri), ("$c", connectionId), ("$now", now));
        else
            Exec("UPDATE apis SET base_url = $b, connection_id = $c, updated_at = $now WHERE id = $id;",
                ("$b", site.AbsoluteUri), ("$c", connectionId), ("$now", now), ("$id", existing.Id));
        return new JsonObject { ["ok"] = true, ["name"] = ApiRow(name).Name };
    }

    /// <summary>A website's tool is "web_" + its name as a tool name (the agents' half comes with the tool itself).</summary>
    public const string WebsiteToolPrefix = "web_";

    // ------------------------------------------------------------------ the fixed six

    private static readonly (string Id, string Summary, bool Destructive, JsonObject Params)[] WebsiteActions =
    [
        ("open", "Open a page of the site and read it", false, Schema(["url"], ("url", "The page: a full address, or a path on the site"))),
        ("read", "Read the page that is open, or part of it", false, Schema([], ("selector", "CSS selector of the part to read (optional)"))),
        ("screenshot", "Take a picture of the page that is open", false, Schema([])),
        ("click", "Click something on the page", true, Schema(["selector"], ("selector", "CSS selector of what to click"))),
        ("fill", "Type into a field on the page", true, Schema(["selector", "text"], ("selector", "CSS selector of the field"), ("text", "What to type"))),
        ("submit", "Send a form on the page", true, Schema([], ("selector", "CSS selector of the form or its button (optional)"))),
    ];

    private static JsonObject Schema(string[] required, params (string Name, string Description)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, d) in props) properties[n] = new JsonObject { ["type"] = "string", ["description"] = d };
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return o;
    }

    /// <summary>What Discover reads for a website: the fixed six, no fetch. Readonly ones are allowed by default; the others are off.</summary>
    private ApiSpec WebsiteSpec(ApiRecord a)
    {
        var actions = WebsiteActions.Select(w => new ApiAction(w.Id, "BROWSER", w.Id, null, w.Summary, w.Params,
            w.Destructive ? new Classification(CommandClass.Destructive, ClassSource.Annotation, "can change things on the site")
                          : new Classification(CommandClass.ReadOnly, ClassSource.Annotation, "only looks at the page"))).ToList();
        Exec("UPDATE apis SET status = $st, updated_at = $now WHERE id = $id;", ("$st", "6 fixed actions"), ("$now", Now()), ("$id", a.Id));
        return new ApiSpec(a.Name, a.BaseUrl, actions, null, 0);
    }

    // ------------------------------------------------------------------ the agents' side

    private const int WebsiteMaxTextChars = 10_000;

    private IEnumerable<ApiRecord> WebsitesOfferedTo(long teamId) => ApiRows().Where(a => a.Kind == "website" && (a.EnabledAll || ApiTeamsOf(a.Id).Contains(teamId)));

    private ApiRecord? WebsiteToolOf(long teamId, string tool) => WebsitesOfferedTo(teamId).FirstOrDefault(a => WebsiteToolPrefix + ToolName(a.Name) == tool);

    private bool IsWebsiteTool(string name) => name.StartsWith(WebsiteToolPrefix, StringComparison.Ordinal) && ApiRows().Any(a => a.Kind == "website" && WebsiteToolPrefix + ToolName(a.Name) == name);

    /// <summary>One tool per website offered to the team with at least one action not denied.</summary>
    private IEnumerable<JsonObject> WebsiteToolsFor(long teamId)
    {
        foreach (var a in WebsitesOfferedTo(teamId).ToList())
        {
            if (HttpUrl(a.BaseUrl) is not { } site) continue;
            var team = ApiRulesOf(a.Id, teamId);
            var global = ApiRulesOf(a.Id, null);
            var open = ApiOps(a.Id).Where(o => !o.Gone && ApiDecide(o, team, global).Decision != Decisions.Deny).ToList();
            if (open.Count == 0) continue;
            var sb = new StringBuilder();
            sb.Append($"Use the person's website {a.Name} ({site.Host}) in a browser on their computer, signed in as them, under the person's rules: some actions run at once, ")
              .Append($"some ask the person first (give {ReasonProperty}; you are woken with the result), some are never allowed. You never get the sign-in or cookies. ")
              .Append($"Pages outside {site.Host} are stopped. Actions:\n");
            foreach (var o in open) sb.Append("- ").Append(o.Id).Append(" — ").Append(Clip(o.Summary, 120)).Append('\n');
            yield return new JsonObject
            {
                ["name"] = WebsiteToolPrefix + ToolName(a.Name),
                ["description"] = sb.ToString().TrimEnd(),
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(WebsiteActions.Select(w => (JsonNode)JsonValue.Create(w.Id)!).ToArray()), ["description"] = "What to do." },
                        ["url"] = new JsonObject { ["type"] = "string", ["description"] = "open: the page, a full address on the site or a path on it." },
                        ["selector"] = new JsonObject { ["type"] = "string", ["description"] = "click, fill, read, submit: a CSS selector." },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "fill: what to type." },
                        [ReasonProperty] = new JsonObject { ["type"] = "string", ["description"] = "Why you need this, in one line (shown to the person if it asks them)." },
                    },
                    ["required"] = new JsonArray("action"),
                    ["additionalProperties"] = false,
                },
            };
        }
    }

    private static string? WebsiteArg(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Checks an action's data and builds the host request, or throws an ArgumentException the agent can read. Nothing is sent here.</summary>
    private WebsiteRequest PlanWebsite(ApiRecord api, ApiOp op, JsonObject args, long teamId, string member)
    {
        var site = HttpUrl(api.BaseUrl) ?? throw new ArgumentException($"{api.Name} has no site address");
        var host = new JsonObject();
        var selector = WebsiteArg(args, "selector");
        if (selector is { Length: > 1000 }) throw new ArgumentException("the selector is too long (1000 characters at most)");
        switch (op.Id)
        {
            case "open":
                var given = WebsiteArg(args, "url") ?? throw new ArgumentException("open needs a url (a full address on the site, or a path on it)");
                if (given.Contains((char)92)) throw new ArgumentException("a web address does not contain a backslash");
                if (!Uri.TryCreate(site, given.Trim(), out var resolved)) throw new ArgumentException($"{Clip(given, 100)} is not a web address");
                if (resolved.Scheme is not ("http" or "https")) throw new ArgumentException($"{Clip(given, 100)} is not a web address (only http and https pages are opened)");
                if (!WithinSite(resolved, site)) throw new ArgumentException($"{Clip(resolved.AbsoluteUri, 200)} is outside {site.AbsoluteUri}");
                host["url"] = resolved.AbsoluteUri;
                break;
            case "click" or "fill":
                if (selector is null) throw new ArgumentException($"{op.Id} needs a selector (CSS)");
                host["selector"] = selector;
                if (op.Id == "fill")
                {
                    var text = WebsiteArg(args, "text") ?? throw new ArgumentException("fill needs text");
                    if (SecretScrubber.Scrub(text) != text) throw new ArgumentException("the text contains a stored secret; it is never typed by an agent");
                    if (text.Length > WebsiteMaxTextChars) throw new ArgumentException($"the text is too long ({WebsiteMaxTextChars} characters at most)");
                    host["text"] = text;
                }
                break;
            default:
                if (selector is not null) host["selector"] = selector;
                break;
        }
        var partition = api.ConnectionId is { } cid ? WebsitePartition(cid) : PublicWebsitePartition(api.Id);
        // one page per member: two agents of a team must not click on each other's page
        return WebsiteRequestFor(op.Id, partition, $"{api.Id}:{teamId}:{member}", site.AbsoluteUri, host);
    }

    private static readonly Regex SignInLooking = new("login|signin|sign-in|auth|account|sso|oauth", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Sends a planned request and turns the window's answer into what the agent reads. Never throws for a window problem.</summary>
    private async Task<ToolReply> SendWebsiteAsync(ApiRecord api, WebsiteRequest request, CancellationToken ct)
    {
        WebsiteReply reply;
        try { reply = await WebsiteHost.RunAsync(request, ct); }
        catch (WebsiteException e) { return new ToolReply($"could not use {api.Name}: {Clip(SecretScrubber.Scrub(e.Message), 300)}", null); }
        catch (RpcException e) { return new ToolReply($"could not use {api.Name}: {Clip(SecretScrubber.Scrub(e.Message), 300)}", null); }
        if (!reply.Ok) return new ToolReply($"could not use {api.Name}: {Clip(SecretScrubber.Scrub(reply.Error ?? "the website window did not do that"), 300)}", null);
        var site = new Uri(request.SiteUrl);
        if (reply.StoppedAt is { } stopped)
        {
            Uri.TryCreate(stopped, UriKind.Absolute, out var to);
            var text = $"stopped: the site sent the page to {to?.Host ?? Clip(stopped, 100)}, outside {site.Host}.";
            if (to is not null && SignInLooking.IsMatch(to.Host + to.AbsolutePath))
            {
                text += $" The website login may need signing in again ({ConnectionsPlace}); stop and tell the person.";
                if (api.ConnectionId is { } cid) MarkLoginNeedsReconnect(cid, request.Partition, $"an agent's page on {site.Host} was sent to {to.Host} at {Stamp()}: sign in again");
            }
            return new ToolReply(text, null);
        }
        var head = reply.Title is { Length: > 0 } t ? (reply.Url is { Length: > 0 } u0 ? $"{t} — {u0}" : t) : reply.Url ?? "";
        if (request.Op == "screenshot" && reply.Png is { Length: > 0 } png)
            return new ToolReply(head.Length > 0 ? SecretScrubber.Scrub(head) + " (screenshot attached)" : "screenshot attached", png);
        var body = SecretScrubber.Scrub(head.Length > 0 ? $"{head}\n\n{reply.Text}" : reply.Text ?? "");
        if (body.Length > CliRunner.MaxOutputChars) body = body[..CliRunner.MaxOutputChars] + "\n(output cut at 64 KB)";
        return new ToolReply(body, null);
    }

    /// <summary>An agent's page landed on a sign-in looking page outside the site: the login is marked so Needs You shows it. Never throws.</summary>
    private void MarkLoginNeedsReconnect(long connectionId, string partition, string reason)
    {
        try
        {
            if (connections is null || Conns.Brief(connectionId) is not { } b || b.Status == "needs_reconnect") return;
            if (Conns.StoreKeyOf(connectionId) is not { } key || WebsitePartitionPrefix + key != partition) return; // the call used another session
            Conns.MarkStatus(connectionId, "needs_reconnect", reason);
        }
        catch (Exception e)
        {
            WebsiteLog($"websites: could not mark connection {connectionId} as needing reconnect ({e.GetType().Name})");
        }
    }

    private async Task<ToolReply> CallWebsiteAsync(RunContext run, ApiRecord api, JsonObject args, CancellationToken ct)
    {
        var action = WebsiteArg(args, "action")?.Trim() ?? throw new ArgumentException("give an action (open, read, screenshot, click, fill or submit)");
        var reason = new[] { WebsiteArg(args, ReasonProperty), WebsiteArg(args, "reason") }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        var team = ApiRulesOf(api.Id, run.Member.TeamId);
        var global = ApiRulesOf(api.Id, null);
        var op = ApiOps(api.Id).FirstOrDefault(o => o.Id == action && !o.Gone);
        if (op is null) return new ToolReply($"refused: no action '{Clip(SecretScrubber.Scrub(action), 100)}' (open, read, screenshot, click, fill, submit)", null);
        var data = new JsonObject();
        foreach (var k in new[] { "url", "selector", "text" }) if (WebsiteArg(args, k) is { } v) data[k] = v;
        var integration = "api:" + api.Name;
        string[] callArgs = [op.Id, SecretScrubber.Scrub(data.ToJsonString())];
        var (decision, rule) = ApiDecide(op, team, global);
        void Record(string d, string? r) =>
            Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, callArgs, d, r, null, Provenance.Actor.Current);
        var ruleText = rule is null ? null : $"{rule.Layer}: {rule.Pattern}";
        if (decision == Decisions.Deny)
        {
            Record(Decisions.Deny, ruleText);
            return new ToolReply($"refused: {api.Name}'s {op.Id} is not allowed for your team. Do not try to work around it.", null);
        }
        WebsiteRequest request;
        try { request = PlanWebsite(api, op, data, run.Member.TeamId, run.Member.Qualified); }
        catch (ArgumentException e) { return new ToolReply("refused: " + SecretScrubber.Scrub(e.Message), null); }
        if (decision == Decisions.Ask && op.Id == "screenshot" && run.Correlation is { } wf && TakeScreenshotGrant(api.Id, run.Member.TeamId, wf) is { } approvedId)
        {
            decision = Decisions.Allow; // the person approved this picture: it runs once, as allowed
            ruleText = $"approved request {approvedId}";
        }
        if (decision == Decisions.Ask)
        {
            if (reason is null) throw new ArgumentException($"this call asks the person first: give a reason ({ReasonProperty}, one line)");
            var id = Scalar(
                """
                INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, action)
                VALUES ($at, $i, $t, $m, $a, $r, $w, $c, $action) RETURNING id;
                """,
                ("$at", Now()), ("$i", integration), ("$t", run.Member.TeamId), ("$m", run.Member.Qualified), ("$a", JsonSerializer.Serialize(callArgs)),
                ("$r", Clip(SecretScrubber.Scrub(reason), 500)), ("$w", run.Correlation), ("$c", run.Causation), ("$action", op.Id));
            Record("asked", ruleText);
            return new ToolReply($"asked: {api.Name}'s {op.Id} waits for the person's approval (request {id}). You will be woken with the result in this workflow. Do other work or end your turn; do not ask again.", null);
        }
        Record(Decisions.Allow, ruleText); // recorded first: an action is never unlogged, whatever the window does
        return await SendWebsiteAsync(api, request, ct);
    }

    /// <summary>One-time permission for a direct screenshot after the person approved an ask for it: (website, team, workflow) to when it ends and the request.</summary>
    private readonly Dictionary<(long Api, long Team, long Workflow), (DateTimeOffset Until, long AskId)> _screenshotGrants = [];
    private readonly Lock _screenshotGate = new();
    private static readonly TimeSpan ScreenshotGrantLife = TimeSpan.FromMinutes(10);

    private void GrantScreenshot(AskRow ask, long apiId)
    {
        lock (_screenshotGate) _screenshotGrants[(apiId, ask.TeamId, ask.Workflow)] = (_clock.GetUtcNow() + ScreenshotGrantLife, ask.Id);
    }

    private long? TakeScreenshotGrant(long apiId, long teamId, long workflow)
    {
        lock (_screenshotGate)
        {
            if (!_screenshotGrants.Remove((apiId, teamId, workflow), out var g)) return null;
            return g.Until >= _clock.GetUtcNow() ? g.AskId : null;
        }
    }

    /// <summary>An approved ask for a website action (args were [op, dataJson]): the same checks and send. A picture cannot ride in a wake message.</summary>
    private async Task<ApiResult> RunApprovedWebsiteAsync(ApiRecord api, AskRow ask, CancellationToken ct)
    {
        var op = ApiOps(api.Id).FirstOrDefault(o => o.Id == ask.Args[0] && !o.Gone);
        if (op is null) return new ApiResult($"refused: {api.Name} has no action '{ask.Args[0]}' any more", null, null, null);
        if (op.Id == "screenshot") {
            GrantScreenshot(ask, api.Id);
            return new ApiResult("approved: call screenshot again now (within 10 minutes) to get the picture", null, null, null);
        }
        JsonObject data;
        try { data = ask.Args.Count > 1 ? JsonNode.Parse(ask.Args[1]) as JsonObject ?? [] : []; }
        catch (JsonException) { return new ApiResult("refused: the request's data could not be read", null, null, null); }
        WebsiteRequest request;
        try { request = PlanWebsite(api, op, data, ask.TeamId, ask.Member); }
        catch (ArgumentException e) { return new ApiResult("refused: " + SecretScrubber.Scrub(e.Message), null, null, null); }
        return new ApiResult((await SendWebsiteAsync(api, request, ct)).Text, null, null, null);
    }
}
