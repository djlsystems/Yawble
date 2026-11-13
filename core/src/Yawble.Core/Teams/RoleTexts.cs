namespace Yawble.Core.Teams;

/// <summary>
/// The two built-in role texts served by skills_get, the same for both CLIs: Teams/Roles/
/// manager.md and member.md, embedded in the core.
/// </summary>
public static class RoleTexts
{
    public static string Manager { get; } = Load("manager");
    public static string Member { get; } = Load("member");

    public static string For(string name) => name.Trim().ToLowerInvariant() switch
    {
        "manager" => Manager,
        "member" => Member,
        _ => throw new ArgumentException($"no role text named '{name}': ask for manager or member"),
    };

    private static string Load(string name)
    {
        using var stream = typeof(RoleTexts).Assembly.GetManifestResourceStream($"Yawble.Core.Roles.{name}.md")
            ?? throw new InvalidOperationException($"role text {name}.md is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
