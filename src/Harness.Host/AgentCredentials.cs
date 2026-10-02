using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A PERSON OR THE OPERATOR CLI MAY DO WITH AN ISSUED CREDENTIAL, answered once for both doors:
/// <c>/api/agents/credentials</c> and <c>/api/agents/{name}/credential</c>, and the request file
/// <see cref="AgentCredentialRequests"/> answers. Each answer is a status and the body the route
/// sends, and no body carries the value or any part of it: a credential answers <c>set</c>,
/// <c>setBy</c> and <c>setAt</c>.
///
/// <para>
/// ONE CREDENTIAL PER COMMAND. A name is a preset (resolved to the command it launches) or a command
/// that at least one preset launches with an issued-credential declaration; setting or clearing
/// through any of them changes the one value every preset of that command uses when its source is
/// issued. The source itself is per preset (<c>agents.credentialSource</c>).
/// </para>
/// </summary>
public sealed class AgentCredentials(
    AgentCatalog catalog, AgentCredentialStore store, TenantSettings settings, AgentAuthProbe? probe = null)
{
    /// <summary>The model presets, in catalog order, each with its command's credential.</summary>
    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        var statuses = await store.AllStatusAsync(ct);
        var presets = Presets();

        return
        [
            .. presets.Select(definition =>
            {
                var command = RunCredentials.CommandOf(definition);
                var status = statuses.GetValueOrDefault(command) ?? CredentialStatus.NotSet;

                return (object)new
                {
                    agent = definition.Name,
                    command,
                    sharedWith = presets
                        .Where(other => !ReferenceEquals(other, definition)
                            && string.Equals(RunCredentials.CommandOf(other), command, StringComparison.OrdinalIgnoreCase))
                        .Select(other => other.Name)
                        .ToArray(),
                    source = SourceName(definition.Name),
                    issuedCredential = definition.IssuedCredential,
                    set = status.Set,
                    setBy = status.SetBy,
                    setAt = status.SetAt,
                };
            }),
        ];
    }

    /// <summary>Sets or replaces the credential of <paramref name="name"/>'s command.</summary>
    public async Task<(int Status, object Body)> SetAsync(
        string name, string? kind, string? value, CredentialActor actor, CancellationToken ct)
    {
        if (Resolve(name) is not ({ } command, { } declaration))
        {
            return Refusal(name);
        }

        kind = string.IsNullOrWhiteSpace(kind) && declaration.Kinds.Count == 1 ? declaration.Kinds[0].Kind : kind;

        if (declaration.Kinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal)) is not { } declared)
        {
            return (400, Error(
                $"`{command}` takes {string.Join(" or ", declaration.Kinds.Select(k => $"`{k.Kind}`"))}, so the kind must be "
                + (declaration.Kinds.Count == 1 ? "that." : "one of those.")));
        }

        if (AgentCredentialStore.ValueRefusal(value, declared) is { } refused)
        {
            return (400, Error(refused));
        }

        CredentialStatus status;
        try
        {
            status = await store.SetAsync(command, declared, value!, actor, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (500, Unrecorded(command, exception));
        }

        probe?.Forget();
        return (200, Answer(command, status));
    }

    /// <summary>Clears the credential of <paramref name="name"/>'s command. Nothing set answers the same, and writes no row.</summary>
    public async Task<(int Status, object Body)> ClearAsync(string name, CredentialActor actor, CancellationToken ct)
    {
        if (Resolve(name) is not ({ } command, not null))
        {
            return Refusal(name);
        }

        try
        {
            await store.ClearAsync(command, actor, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (500, Unrecorded(command, exception));
        }

        probe?.Forget();
        return (200, Answer(command, CredentialStatus.NotSet));
    }

    /// <summary>A preset's source and its command's credential, by preset or command name.</summary>
    public async Task<(int Status, object Body)> StatusAsync(string name, CancellationToken ct)
    {
        if (Resolve(name) is not ({ } command, not null))
        {
            return Refusal(name);
        }

        var preset = catalog.Definition(name)?.Name;
        return (200, Described(preset, command, await store.StatusAsync(command, ct)));
    }

    /// <summary>Switches <paramref name="preset"/> to <paramref name="source"/> through the
    /// <c>agents.credentialSource</c> setting, with its tenant row as every setting write has.</summary>
    public async Task<(int Status, object Body)> SourceAsync(string preset, string? source, CredentialActor actor, CancellationToken ct)
    {
        if (catalog.Definition(preset) is not { } definition)
        {
            return (404, Error($"'{preset}' is not an Agent preset this instance has."));
        }

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in settings.AgentCredentialSources)
        {
            if (!string.Equals(name, definition.Name, StringComparison.OrdinalIgnoreCase)) map[name] = value;
        }

        map[definition.Name] = source ?? "";

        try
        {
            await settings.WriteAsync(
                new Dictionary<string, JsonElement>
                {
                    [TenantSettings.AgentCredentialSourceName] = JsonSerializer.SerializeToElement(map),
                },
                actor.Id, actor.Email, ct);
        }
        catch (TenantSettingRejected rejected)
        {
            return (400, Error(rejected.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (500, Error($"'{definition.Name}''s source was not changed: its record could not be written ({exception.Message})."));
        }

        probe?.Forget();

        if (definition.IssuedCredential is null)
        {
            return (200, Described(definition.Name, RunCredentials.CommandOf(definition), CredentialStatus.NotSet));
        }

        var command = RunCredentials.CommandOf(definition);
        return (200, Described(definition.Name, command, await store.StatusAsync(command, ct)));
    }

    /// <summary>
    /// Why the catalog's issued-credential declarations cannot stand, or null: one that is not
    /// usable (<see cref="IssuedCredential.Refusal"/>), or two presets launching one command that
    /// declare different kinds - one stored value would be placed in two different variables.
    /// </summary>
    public static string? DeclarationRefusal(IReadOnlyList<AgentDefinition> catalog)
    {
        foreach (var definition in catalog)
        {
            if (definition.IssuedCredential is { } declaration && IssuedCredential.Refusal(definition.Name, declaration) is { } refusal)
            {
                return refusal;
            }
        }

        foreach (var group in catalog.Where(d => d.IssuedCredential is not null && d.Launch is not null)
                     .GroupBy(RunCredentials.CommandOf, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (group.Skip(1).FirstOrDefault(other => !other.IssuedCredential!.SameKinds(first.IssuedCredential!)) is { } differs)
            {
                return $"'{differs.Name}' and '{first.Name}' both launch `{group.Key}` but declare different issued-credential "
                    + "kinds; one credential is stored per command, so they must declare the same kinds and variables.";
            }
        }

        return null;
    }

    /// <summary>
    /// The command <paramref name="name"/> names and the declaration its credential follows: a
    /// preset's own, or for a command the first preset launching it that declares one. Command null
    /// when the name is neither a preset nor a command a preset launches; declaration null when
    /// nothing launching it declares one.
    /// </summary>
    public (string? Command, IssuedCredential? Declaration) Resolve(string name)
    {
        if (catalog.Definition(name) is { } preset)
        {
            return (RunCredentials.CommandOf(preset), preset.IssuedCredential);
        }

        var launching = catalog.Definitions
            .Where(d => d.Launch is not null && string.Equals(RunCredentials.CommandOf(d), name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (launching.Count == 0) return (null, null);

        return (RunCredentials.CommandOf(launching[0]), launching.FirstOrDefault(d => d.IssuedCredential is not null)?.IssuedCredential);
    }

    private (int Status, object Body) Refusal(string name) => Resolve(name) switch
    {
        (null, _) => (404, Error($"'{name}' is neither an Agent preset nor a command one launches.")),
        var (command, _) => (400, Error(
            $"`{command}` takes no issued credential: no preset launching it declares one, so it signs in only through the shared home.")),
    };

    private string SourceName(string preset) =>
        settings.CredentialSourceOf(preset) == CredentialSource.Issued ? TenantSettings.IssuedSource : TenantSettings.HomeSource;

    private object Described(string? preset, string command, CredentialStatus status) => new
    {
        agent = preset,
        command,
        source = preset is null ? null : SourceName(preset),
        set = status.Set,
        setBy = status.SetBy,
        setAt = status.SetAt,
    };

    private static object Answer(string command, CredentialStatus status) =>
        new { command, set = status.Set, setBy = status.SetBy, setAt = status.SetAt };

    private static object Unrecorded(string command, Exception exception) =>
        Error($"The credential for `{command}` was not changed: its record could not be written ({exception.Message}).");

    private static object Error(string error) => new { error };

    private IReadOnlyList<AgentDefinition> Presets() =>
        [.. catalog.Definitions.Where(d => d.Launch is { LanguageModel: true } && !d.Hidden)];
}
