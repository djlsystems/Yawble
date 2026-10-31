using System.Text;
using System.Text.Json;
using Harness.Contracts;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// THE KINDS OF CONNECTION, as the wire spells them. A connection has one: a sign-in at an OAuth
/// provider (<c>oauth</c>, provider <c>google</c>, <c>microsoft</c> or a custom one), a mailbox with an
/// app password (<c>imap</c>), a website (<c>website</c>, whose fields a later release defines), an API
/// key (<c>apikey</c>) or a username and password (<c>login</c>). For every kind but <c>oauth</c> the
/// <c>connections.provider</c> column is the kind itself, as it has always been for a mailbox.
/// </summary>
public static class ConnectionKinds
{
    public const string OAuth = "oauth";

    public const string Mailbox = MailboxSettings.Kind;

    public const string Website = "website";

    public const string ApiKey = "apikey";

    public const string Login = "login";

    /// <summary>The kinds whose fields are a <see cref="CredentialSettings"/>: no sign-in, no servers.</summary>
    public static IReadOnlyList<string> Credentials { get; } = [Login, ApiKey, Website];

    public static bool IsCredential(string kind) => Credentials.Contains(kind, StringComparer.Ordinal);

    /// <summary>A connection's kind from its provider column.</summary>
    public static string Of(string provider) => provider == Mailbox || IsCredential(provider) ? provider : OAuth;

    /// <summary>"username and password", "API key", "website", as a sentence names a kind.</summary>
    public static string Display(string kind) => kind switch
    {
        Login => "username and password",
        ApiKey => "API key",
        Website => "website",
        Mailbox => "mailbox (IMAP)",
        _ => "sign-in",
    };
}

/// <summary>One field of a credential connection as every answer shows it: a masked field's value is
/// never in it, only whether one is <see cref="Set"/>.</summary>
public sealed record CredentialField(string Name, string Label, bool Masked, string? Value, bool Set);

/// <summary>A credential connection's preset (null for a kind with only one form) and its fields.
/// Never a masked value.</summary>
public sealed record CredentialSettings(string? Preset, IReadOnlyList<CredentialField> Fields)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The fields as their row's <c>fields</c> column holds them.</summary>
    public string FieldsJson() => JsonSerializer.Serialize(Fields, Json);

    public static IReadOnlyList<CredentialField> ReadFields(string json) =>
        JsonSerializer.Deserialize<List<CredentialField>>(json, Json) ?? [];

    public object View() => new
    {
        preset = Preset,
        fields = Fields.Select(f => new { name = f.Name, label = f.Label, masked = f.Masked, value = f.Masked ? null : f.Value, set = f.Set }),
    };
}

/// <summary>
/// A field a preset names: what it is called, whether its value is masked (a secret, stored as
/// ciphertext and never answered) and whether a connection must have it. A website's fields say more:
/// <see cref="Type"/> is <c>text</c>, <c>url</c> (a bare address is given <c>https://</c>), <c>enum</c>
/// (one of <see cref="Options"/>) or <c>bool</c> (<c>true</c> or <c>false</c>); <see cref="Advanced"/>
/// fields sit under Advanced; a <see cref="ReadOnly"/> one is never typed - the platform sets it (a
/// website's saved session, kept by Sign in); <see cref="Default"/> is the value a field left out has;
/// <see cref="Help"/> is the sentence beside it.
/// </summary>
public sealed record CredentialFieldSpec(
    string Name, string Label, bool Masked, bool Required, string Type = CredentialFieldSpec.Text,
    IReadOnlyList<string>? Options = null, bool Advanced = false, bool ReadOnly = false, string? Default = null, string? Help = null)
{
    public const string Text = "text";

    public const string Url = "url";

    public const string Enum = "enum";

    public const string Bool = "bool";
}

/// <summary>
/// A form a credential kind is made from: the fields a service needs, each masked or not. A preset
/// with <see cref="NamedFields"/> (API key's Other) takes the fields the person names instead.
/// <see cref="Test"/> is the one harmless request Test connection sends; null, and it has no test.
/// </summary>
public sealed record CredentialPreset(
    string Kind, string? Id, string Name, IReadOnlyList<CredentialFieldSpec> Fields, bool NamedFields = false, CredentialTest? Test = null)
{
    public object View() => new
    {
        kind = Kind,
        id = Id,
        name = Name,
        namedFields = NamedFields,
        fields = Fields.Select(f => new
        {
            name = f.Name, label = f.Label, masked = f.Masked, required = f.Required, type = f.Type, options = f.Options,
            advanced = f.Advanced, readOnly = f.ReadOnly, @default = f.Default, help = f.Help,
        }),
        test = Test is not null || Kind == ConnectionKinds.Website,
    };
}

/// <summary>
/// THE ONE HARMLESS REQUEST a preset's Test connection sends: a GET of <see cref="Url"/>, whose
/// <c>{field}</c> marks are the connection's values (a bare <c>{url}</c> is the address as typed), signed
/// with HTTP Basic (<see cref="BasicUser"/> and <see cref="BasicPassword"/> name fields; no password
/// field sends an empty one) or a <see cref="Bearer"/> token (<see cref="OnlyMasked"/>: the one masked
/// field). A 401 or 403 is a refusal, and so is a body matching <see cref="RefusedWhen"/>, with the
/// first group of <see cref="ReasonFrom"/> as its reason. <see cref="What"/> is what a sentence says
/// was refused or accepted, by the service's name - or, for a request to the connection's own
/// <c>{url}</c>, by that address's host. A preset whose test names a field the connection has not set has no test.
/// </summary>
public sealed partial record CredentialTest(
    string Url, string What, string? BasicUser = null, string? BasicPassword = null, string? Bearer = null,
    string? RefusedWhen = null, string? ReasonFrom = null)
{
    public const string OnlyMasked = "*";

    /// <summary>The probe for a connection's values, or null when it has no test: a field the request
    /// names is not set, or (Other) it has no <c>url</c> or not exactly one masked field.</summary>
    public HttpProbe? Probe(CredentialPreset preset, CredentialSettings settings, IReadOnlyDictionary<string, string> secrets)
    {
        string? Value(string name) => settings.Fields.FirstOrDefault(f => f.Name == name && f.Set) is { } field
            ? field.Masked ? secrets.GetValueOrDefault(name) : field.Value
            : null;

        var masked = settings.Fields.Where(f => f.Masked && f.Set && f.Name != "url").ToList();

        var unset = false;
        var url = Url == "{url}" ? Value("url") : FieldMark().Replace(Url, m =>
        {
            if (Value(m.Groups[1].Value) is { } value) return Uri.EscapeDataString(value);
            unset = true;
            return "";
        });

        if (url is null || unset || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (BasicUser is not null)
        {
            if (Value(BasicUser) is not { } user) return null;
            var password = BasicPassword is null ? "" : Value(BasicPassword);
            if (password is null) return null;
            headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        }
        else if (Bearer is not null)
        {
            var token = Bearer == OnlyMasked ? masked.Count == 1 ? secrets.GetValueOrDefault(masked[0].Name) : null : Value(Bearer);
            if (token is null) return null;
            headers["Authorization"] = "Bearer " + token;
        }

        List<string> hidden = [.. settings.Fields.Where(f => f.Masked && f.Set).Select(f => secrets.GetValueOrDefault(f.Name)).OfType<string>(), .. headers.Values];

        // A request to the person's own address names the address; a service's, the service.
        return new HttpProbe(Url == "{url}" ? uri.Host : preset.Name, What, url, headers, hidden, RefusedWhen, ReasonFrom);
    }

    /// <summary>Whether a connection's settings give this request all it names, without reading a
    /// masked value: what the button is shown for.</summary>
    public bool Applies(CredentialSettings settings)
    {
        var placeholders = settings.Fields.Where(f => f.Set).ToDictionary(f => f.Name, f => f.Masked ? "masked" : f.Value ?? "", StringComparer.Ordinal);
        return Probe(new CredentialPreset("", null, "", []), settings, placeholders) is not null;
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_.-]*)\}")]
    private static partial Regex FieldMark();
}

/// <summary>The forms of the credential kinds.</summary>
public static class CredentialPresets
{
    public const string Other = "other";

    public static IReadOnlyList<CredentialPreset> All { get; } =
    [
        // Stripe: the account's balance, read with the secret key as a bearer token - https://docs.stripe.com/api/balance/balance_retrieve.
        new(ConnectionKinds.ApiKey, "stripe", "Stripe", [new("key", "Secret key", true, true)],
            Test: new("https://api.stripe.com/v1/balance", "secret key", Bearer: "key")),
        // Namecheap: the account's balances. It answers 200 with Status="ERROR" for a key it refuses, a
        // user it does not know or an address not on the key's whitelist - https://www.namecheap.com/support/api/methods/users/get-balances/.
        // ClientIp must be the public IPv4 the call leaves from and one the person whitelisted (else 1011150,
        // Invalid request IP) - https://www.namecheap.com/support/api/intro/ - so the person names it.
        new(ConnectionKinds.ApiKey, "namecheap", "Namecheap",
            [new("apiUser", "API user", false, true), new("apiKey", "API key", true, true), new("clientIp", "Whitelisted IP", false, true)],
            Test: new(
                "https://api.namecheap.com/xml.response?ApiUser={apiUser}&ApiKey={apiKey}&UserName={apiUser}&ClientIp={clientIp}&Command=namecheap.users.getBalances",
                "API key", RefusedWhen: "Status=\"ERROR\"", ReasonFrom: "<Error[^>]*>([^<]*)</Error>")),
        // Twilio: the account itself, read with the API key SID and secret - https://www.twilio.com/docs/iam/api/account#fetch-an-account-resource.
        new(ConnectionKinds.ApiKey, "twilio", "Twilio",
            [new("accountSid", "Account SID", false, true), new("apiKeySid", "API key SID", false, true), new("apiKeySecret", "API key secret", true, true)],
            Test: new("https://api.twilio.com/2010-04-01/Accounts/{accountSid}.json", "API key", BasicUser: "apiKeySid", BasicPassword: "apiKeySecret")),
        // Other: tested only when it has a `url` field and one masked field, sent as a bearer token.
        new(ConnectionKinds.ApiKey, Other, "Other", [], NamedFields: true, Test: new("{url}", "API key", Bearer: CredentialTest.OnlyMasked)),
        // Username and password: tested only with a URL, signed in to with HTTP Basic.
        new(ConnectionKinds.Login, null, "Username and password",
            [new("username", "Username", false, true), new("password", "Password", true, true), new("url", "URL", false, false)],
            Test: new("{url}", "username and password", BasicUser: "username", BasicPassword: "password")),
        // A website: what the platform's browser signs in with, and the page that shows it is signed in.
        // Its test is that signed-in check, run in the browser on a worker (WebsiteFields).
        new(ConnectionKinds.Website, null, "Website", WebsiteFields.All),
    ];

    /// <summary>
    /// Whether a credential connection has a test: its preset declares one, and it has what that test
    /// names. A website always has one - its signed-in check - which says itself what it lacks.
    /// </summary>
    public static bool Testable(string kind, CredentialSettings settings) =>
        kind == ConnectionKinds.Website || (For(kind, settings.Preset)?.Test is { } test && test.Applies(settings));

    public static CredentialPreset? For(string kind, string? preset) =>
        All.FirstOrDefault(p => p.Kind == kind && string.Equals(p.Id, preset, StringComparison.Ordinal));

    /// <summary>The ids a kind's preset may be: none for a kind with one form.</summary>
    public static IReadOnlyList<string> IdsOf(string kind) => [.. All.Where(p => p.Kind == kind && p.Id is not null).Select(p => p.Id!)];
}

/// <summary>A credential connection as a run is handed it: every field's value, masked ones included.
/// Its printed form leaves the values out.</summary>
public sealed record CredentialGrant(string Kind, string? Preset, IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> Masked)
{
    /// <summary>The values a run's redaction set takes: every masked field's.</summary>
    public IReadOnlyList<string> Secrets => [.. Values.Where(v => Masked.Contains(v.Key)).Select(v => v.Value)];

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Preset = {Preset}, Fields = [{string.Join(' ', Values.Keys)}]");
        return true;
    }
}

/// <summary>A field as a request carries it. On an edit a null <see cref="Value"/> keeps the stored one;
/// <see cref="Masked"/> is read only for API key's Other, whose fields the person names.</summary>
public sealed record CredentialFieldValue(string? Name, string? Value, bool? Masked = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Masked = {Masked}");
        return true;
    }
}

/// <summary>Body of <c>POST /api/connections/credentials</c>.</summary>
public sealed record AddCredential(string? Kind, string? Preset, string? Name, IReadOnlyList<CredentialFieldValue>? Fields);

/// <summary>Body of <c>PUT /api/connections/{id}/credentials</c>.</summary>
public sealed record EditCredential(IReadOnlyList<CredentialFieldValue>? Fields);

/// <summary>
/// WHAT A CREDENTIAL CONNECTION'S FIELDS BECOME, checked: the settings every answer may show and the
/// masked values the store seals. Every refusal names the field, and none carries a value.
/// </summary>
public static partial class CredentialForm
{
    public const int MaximumFields = 20;

    public const int MaximumValueLength = 4096;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex FieldName();

    /// <summary>The fields of a new connection, or of an edit over <paramref name="before"/> and its
    /// <paramref name="stored"/> masked values. Answers the settings, the masked values, the names of
    /// the fields whose value changed, or the refusal.</summary>
    public static (CredentialSettings? Settings, Dictionary<string, string>? Secrets, IReadOnlyList<string> Changed, string? Error) Read(
        CredentialPreset preset, IReadOnlyList<CredentialFieldValue>? given,
        CredentialSettings? before = null, IReadOnlyDictionary<string, string>? stored = null)
    {
        given ??= [];
        if (given.Any(f => f is null)) return (null, null, [], "`fields` holds `{ name, value }` objects.");

        if (given.GroupBy(f => f.Name?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return (null, null, [], $"`fields` names `{twice.Key}` more than once.");
        }

        IReadOnlyList<CredentialFieldSpec> specs;

        if (preset.NamedFields)
        {
            if (given.Count is 0 or > MaximumFields) return (null, null, [], $"`fields` names 1 to {MaximumFields} fields for an API key of another service.");

            var named = new List<CredentialFieldSpec>();
            foreach (var field in given)
            {
                var name = field.Name?.Trim() ?? "";
                if (!FieldName().IsMatch(name))
                {
                    return (null, null, [], "A field's `name` is 1 to 64 letters, digits, `_`, `.` or `-`, starting with a letter.");
                }

                var previous = before?.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
                named.Add(new CredentialFieldSpec(name, name, field.Masked ?? previous?.Masked ?? true, true));
            }

            specs = named;
        }
        else
        {
            if (given.FirstOrDefault(f => !preset.Fields.Any(s => s.Name == f.Name?.Trim())) is { } unknown)
            {
                return (null, null, [], preset.Fields.Count == 0
                    ? $"A {ConnectionKinds.Display(preset.Kind)} connection has no fields yet; `{unknown.Name}` is not one."
                    : $"`{unknown.Name}` is not a field of {preset.Name}. It has: {string.Join(", ", preset.Fields.Select(f => f.Name))}.");
            }

            specs = preset.Fields;
        }

        var fields = new List<CredentialField>();
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var spec in specs)
        {
            var value = given.FirstOrDefault(f => f.Name?.Trim() == spec.Name)?.Value;
            var previous = before?.Fields.FirstOrDefault(f => f.Name == spec.Name && f.Masked == spec.Masked);
            var kept = previous is null ? null : spec.Masked ? stored?.GetValueOrDefault(spec.Name) : previous.Value;

            // A FIELD THE PLATFORM SETS (a website's saved session, kept by Sign in) is never typed.
            if (spec.ReadOnly && value is not null)
            {
                return (null, null, [], $"`{spec.Name}` ({spec.Label}) is not typed: {spec.Help ?? "the platform sets it."}");
            }

            if (value is null)
            {
                // AN EDIT KEEPS what it does not name; a new connection has nothing to keep but a default.
                value = kept ?? (before is null || previous is null ? spec.Default : null);
            }
            else
            {
                if (spec.Masked ? value.Length == 0 : value.Trim().Length == 0)
                {
                    if (spec.Required) return (null, null, [], $"`{spec.Name}` ({spec.Label}) is required.");
                    value = spec.Default;
                }
                else if (Refusal(spec, spec.Masked ? value : Normal(spec, value.Trim())) is { } refusal)
                {
                    return (null, null, [], refusal);
                }
                else
                {
                    value = spec.Masked ? value : Normal(spec, value.Trim());
                }

                if (!string.Equals(value, kept, StringComparison.Ordinal)) changed.Add(spec.Name);
            }

            if (value is null && spec.Required) return (null, null, [], $"`{spec.Name}` ({spec.Label}) is required.");

            if (spec.Masked && value is not null) secrets[spec.Name] = value;
            fields.Add(new CredentialField(spec.Name, spec.Label, spec.Masked, spec.Masked ? null : value, value is not null));
        }

        // A NAMED FIELD LEFT OUT of an edit of Other is removed: that, too, is a change.
        if (before is not null)
        {
            changed.AddRange(before.Fields.Where(f => f.Set && !fields.Any(n => n.Name == f.Name && n.Masked == f.Masked)).Select(f => f.Name));
        }

        return (new CredentialSettings(preset.Id, fields), secrets, [.. changed.Distinct(StringComparer.Ordinal)], null);
    }

    /// <summary>What a connection's account reads as: a login's username, an API key's first unmasked
    /// field, else the service's name; a website's, its site's host.</summary>
    public static string AccountOf(CredentialPreset preset, CredentialSettings settings) =>
        preset.Kind == ConnectionKinds.Website ? WebsiteFields.AccountOf(settings)
        : settings.Fields.FirstOrDefault(f => !f.Masked && f.Value is { Length: > 0 } && f.Name != "url")?.Value
            ?? (preset.Kind == ConnectionKinds.ApiKey ? preset.Name : "");

    /// <summary>
    /// A value as it is kept: a <c>url</c> field's bare address (<c>mysite.com/login</c>) given
    /// <c>https://</c>, a path or any other scheme as written; a <c>bool</c> in lower case; an
    /// <c>enum</c> as its option is spelled; a website's languages each in their canonical case.
    /// </summary>
    private static string Normal(CredentialFieldSpec spec, string value) => spec.Type switch
    {
        CredentialFieldSpec.Url => WebsiteFields.WithScheme(value),
        CredentialFieldSpec.Bool => value.ToLowerInvariant(),
        CredentialFieldSpec.Enum => spec.Options?.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase)) ?? value,
        _ when spec.Name == WebsiteFields.Languages => WebsiteFields.CanonicalLanguages(value),
        _ => value,
    };

    private static string? Refusal(CredentialFieldSpec spec, string value)
    {
        if (value.Length > MaximumValueLength) return $"`{spec.Name}` ({spec.Label}) is longer than {MaximumValueLength} characters.";
        if (spec.Type == CredentialFieldSpec.Enum && spec.Options is { } options && !options.Contains(value, StringComparer.Ordinal))
        {
            return $"`{spec.Name}` ({spec.Label}) is one of {string.Join(", ", options)}.";
        }

        if (spec.Type == CredentialFieldSpec.Bool && value is not ("true" or "false")) return $"`{spec.Name}` ({spec.Label}) is true or false.";
        if (WebsiteFields.Refusal(spec, value) is { } website) return website;
        if (value.Any(char.IsControl)) return $"`{spec.Name}` ({spec.Label}) holds a control character.";

        // A MASKED VALUE IS REDACTED from everything a plugin writes, which a value this short cannot be.
        if (spec.Masked && value.Length < PluginMemberRunner.MinimumSecretLength)
        {
            return $"`{spec.Name}` ({spec.Label}) is at least {PluginMemberRunner.MinimumSecretLength} characters.";
        }

        if (spec.Name == "url" && !(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            return "`url` is a web address starting with http:// or https://.";
        }

        return null;
    }
}

/// <summary>A write refused because another connection already has the name: every name is unique,
/// ignoring case. Thrown inside the write's transaction, so nothing of it lands.</summary>
public sealed class ConnectionNameTakenException(string name)
    : InvalidOperationException($"Another connection is already called '{name}'. Every connection has its own name.")
{
    public string Name { get; } = name;
}
