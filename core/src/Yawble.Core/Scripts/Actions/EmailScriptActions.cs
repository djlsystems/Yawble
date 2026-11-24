using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using IntegrationCaller = Yawble.Core.Integrations.ScriptCaller;
using RunCaller = Yawble.Core.Scripts.Running.ScriptCaller;

namespace Yawble.Core.Scripts.Actions;

/// <summary>What an Email action does to the mailbox: reads it, changes it (a dry run does not perform these), or sends mail (phase 2: under the send policy).</summary>
public enum EmailActionClass
{
    Read,
    Change,
    Send,
}

/// <summary>One action of the Email integration: its name (as a script's <c>requires</c> lists it), what it does, its arguments and result.</summary>
public sealed record EmailActionSpec(string Name, EmailActionClass Class, ScriptActionSignature Signature);

/// <summary>
/// The Email integration's actions for scripts (kind "email", scripts-design.md section 9), pinned in docs/connections-and-integrations.md.
/// Phase 2 adds send and reply here as <see cref="EmailActionClass.Send"/>; there is no delete action.
/// </summary>
public static class EmailActions
{
    public const string Kind = "email";

    public const string List = "list";
    public const string Get = "get";
    public const string Search = "search";
    public const string Thread = "thread";
    public const string MarkRead = "markRead";
    public const string Move = "move";

    private static ScriptActionSignature Sig(string args, string result) =>
        new(JsonNode.Parse(args)!.AsObject(), result);

    public static readonly IReadOnlyList<EmailActionSpec> All =
    [
        new(List, EmailActionClass.Read, Sig("""
            { "type": "object", "properties": {
                "folder": { "type": "string", "description": "The folder, e.g. INBOX." },
                "after": { "type": ["string", "null"], "description": "For polling: a cursor from an earlier list of this folder; only messages that came after it, in arrival order. Not with since." },
                "since": { "type": ["string", "null"], "format": "date-time", "description": "A time filter: only messages received later than this (exclusive), by date. Not for polling: use after." },
                "unreadOnly": { "type": ["boolean", "null"], "description": "Only unread messages." },
                "max": { "type": ["integer", "null"], "description": "Return the first this many of those (at most 200); with after, the rest come on the next poll." } },
              "required": ["folder"] }
            """, "list<EmailMessage>")),
        new(Get, EmailActionClass.Read, Sig("""
            { "type": "object", "properties": {
                "id": { "type": "string", "description": "The message's id." },
                "folder": { "type": ["string", "null"], "description": "Where to look (optional; the id is enough)." } },
              "required": ["id"] }
            """, "EmailMessage")),
        new(Search, EmailActionClass.Read, Sig("""
            { "type": "object", "properties": {
                "folder": { "type": "string", "description": "The folder, e.g. INBOX." },
                "query": { "type": "string", "description": "Gmail: Gmail's search syntax. Otherwise: words anywhere in the message." } },
              "required": ["folder", "query"] }
            """, "list<EmailMessage>")),
        new(Thread, EmailActionClass.Read, Sig("""
            { "type": "object", "properties": {
                "threadId": { "type": "string", "description": "A message's threadId." } },
              "required": ["threadId"] }
            """, "list<EmailMessage>")),
        new(MarkRead, EmailActionClass.Change, Sig("""
            { "type": "object", "properties": {
                "id": { "type": "string", "description": "The message's id." },
                "folder": { "type": ["string", "null"], "description": "Where to look (optional; the id is enough)." } },
              "required": ["id"] }
            """, "EmailMessage")),
        new(Move, EmailActionClass.Change, Sig("""
            { "type": "object", "properties": {
                "id": { "type": "string", "description": "The message's id." },
                "folder": { "type": "string", "description": "The folder to move it to (Gmail: the label)." } },
              "required": ["id", "folder"] }
            """, "EmailMessage")),
    ];

    public static EmailActionSpec? Find(string action) => All.FirstOrDefault(a => a.Name == action);
}

/// <summary>The "email" kind's catalog: the fixed actions above; changing unless a read; each with its signature.</summary>
public sealed class EmailScriptActionCatalog : IScriptActionCatalog, IScriptActionSignatures
{
    public static readonly EmailScriptActionCatalog Instance = new();

    public bool HasAction(string kind, string action) => kind == EmailActions.Kind && EmailActions.Find(action) is not null;

    public bool IsChanging(string kind, string action) =>
        kind != EmailActions.Kind || EmailActions.Find(action) is not { Class: EmailActionClass.Read };

    public ScriptActionSignature? Signature(string kind, string action) => kind == EmailActions.Kind ? EmailActions.Find(action)?.Signature : null;
}

/// <summary>What the script's Email actions need from the integrations: one call on a signed-in mailbox. <see cref="IntegrationScriptEmail"/> is the real one.</summary>
public interface IScriptEmail
{
    /// <summary><see cref="IntegrationService.UseEmailForScriptAsync"/>: refusals throw <see cref="ScriptApiRefusedException"/>.</summary>
    Task<T> UseAsync<T>(IntegrationCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct);
}

/// <summary><see cref="IScriptEmail"/> over the app's <see cref="IntegrationService"/>, which is made after the script runner: a call waits for it.</summary>
public sealed class IntegrationScriptEmail(Task<IntegrationService> service) : IScriptEmail
{
    public IntegrationScriptEmail(IntegrationService service) : this(Task.FromResult(service)) { }

    public async Task<T> UseAsync<T>(IntegrationCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct)
    {
        var svc = await service.WaitAsync(ct);
        return await svc.UseEmailForScriptAsync(caller, integration, action, args, work, ct);
    }
}

public sealed class EmailScriptActionsProvider(IScriptEmail email) : IScriptActionsProvider
{
    public IScriptActionCatalog Catalog => EmailScriptActionCatalog.Instance;

    public IScriptActions For(RunCaller caller) => new EmailScriptActions(email, caller);
}

/// <summary>
/// One run's Email actions: the step's <c>use</c> is bound (per member) to one of the team's Email integrations. The args are checked against
/// the action's signature before anything is opened; a refusal, a bad argument or a mail failure fails the step with its reason. A changing
/// action in a dry run without a catalog is not performed (the engine's "would" result).
/// </summary>
public sealed class EmailScriptActions(IScriptEmail email, RunCaller caller) : IScriptActions, IScriptActionCatalog, IScriptActionSignatures
{
    public bool HasAction(string kind, string action) => EmailScriptActionCatalog.Instance.HasAction(kind, action);

    public bool IsChanging(string kind, string action) => EmailScriptActionCatalog.Instance.IsChanging(kind, action);

    public ScriptActionSignature? Signature(string kind, string action) => EmailScriptActionCatalog.Instance.Signature(kind, action);

    public async Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
    {
        if (call.Kind != EmailActions.Kind) throw new ScriptActionException($"{call.Use}: '{call.Kind}' is not email");
        var spec = EmailActions.Find(call.Action) ?? throw new ScriptActionException($"{call.Use}: email has no action '{Clip(call.Action)}'");
        var args = call.Args ?? [];
        CheckArgs(call.Use, spec, args);
        if (!caller.Bindings.TryGetValue(call.Use, out var integration) || string.IsNullOrWhiteSpace(integration))
            throw new ScriptActionException($"'{call.Use}' is not bound to one of {caller.Team}'s Email integrations");
        if ((caller.DryRun || call.DryRun) && spec.Class != EmailActionClass.Read)
            return new JsonObject { ["dryRun"] = true, ["wouldCall"] = spec.Name };

        var who = new IntegrationCaller(caller.Team, caller.Member ?? ApiScriptActions.TestRunMember,
            caller.RunId.ToString(CultureInfo.InvariantCulture), caller.DryRun || call.DryRun);
        try
        {
            return await email.UseAsync<JsonNode?>(who, integration, spec.Name, args, mailbox => RunAsync(mailbox, spec.Name, args, ct), ct);
        }
        catch (ScriptApiRefusedException e) { throw new ScriptActionException(e.Message); }
        catch (MailException e) { throw new ScriptActionException($"{call.Use} {spec.Name}: {e.Message}"); }
        catch (ArgumentException e) { throw new ScriptActionException($"{call.Use} {spec.Name}: {e.Message}"); }
    }

    private static async Task<JsonNode?> RunAsync(EmailMailbox m, string action, JsonObject a, CancellationToken ct) => action switch
    {
        EmailActions.List => Many(await m.ListAsync(Text(a, "folder")!, Instant(a, "since"), Bool(a, "unreadOnly") ?? false, Int(a, "max"), Text(a, "after"), ct)),
        EmailActions.Get => (await m.GetAsync(Text(a, "id")!, Text(a, "folder"), ct)).ToJson(),
        EmailActions.Search => Many(await m.SearchAsync(Text(a, "folder")!, Text(a, "query")!, ct)),
        EmailActions.Thread => Many(await m.ThreadAsync(Text(a, "threadId")!, ct)),
        EmailActions.MarkRead => (await m.MarkReadAsync(Text(a, "id")!, Text(a, "folder"), ct)).ToJson(),
        EmailActions.Move => (await m.MoveAsync(Text(a, "id")!, Text(a, "folder")!, null, ct)).ToJson(),
        _ => throw new ArgumentException($"no action '{action}'"),
    };

    private static JsonArray Many(IReadOnlyList<EmailMessage> list) => new(list.Select(m => (JsonNode)m.ToJson()).ToArray());

    /// <summary>The args against the signature: no other argument, the required ones there, each of its JSON type; else the step fails.</summary>
    public static void CheckArgs(string use, EmailActionSpec spec, JsonObject args)
    {
        var props = spec.Signature.Args["properties"] as JsonObject ?? [];
        var unknown = args.Select(p => p.Key).Where(k => !props.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            throw new ScriptActionException($"{use} {spec.Name}: no argument '{Clip(unknown[0])}' (it takes {string.Join(", ", props.Select(p => p.Key))})");
        var problems = JsonSchemaValidator.Validate(spec.Signature.Args, args);
        if (problems.Count > 0) throw new ScriptActionException($"{use} {spec.Name}: {problems[0]}");
        foreach (var (key, value) in args)
            if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim().Length == 0 && Required(spec, key))
                throw new ScriptActionException($"{use} {spec.Name}: '{key}' is empty");
        if (Int(args, "max") is < 0) throw new ScriptActionException($"{use} {spec.Name}: max cannot be negative");
        if (spec.Name == EmailActions.List && Text(args, "after") is not null && Text(args, "since") is not null)
            throw new ScriptActionException($"{use} {spec.Name}: give after or since, not both (poll with after)");
    }

    private static bool Required(EmailActionSpec spec, string key) =>
        (spec.Signature.Args["required"] as JsonArray)?.Any(r => (string?)r == key) == true;

    private static string? Text(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? Int(JsonObject a, string key) =>
        a[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Clamp(decimal.Truncate(d), int.MinValue, int.MaxValue) : null;

    private static DateTimeOffset? Instant(JsonObject a, string key) =>
        Text(a, key) is { } s ? ScriptValues.TryParseInstant(s, out var at) ? at : throw new ArgumentException($"since '{Clip(s)}' is not a date-time") : null;

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "...";
}
