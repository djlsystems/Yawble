namespace Yawble.CodeBrowser;

/// <summary>A refusal the code browser answers with: <see cref="Code"/> is the RPC error code.</summary>
public sealed class CodeBrowserException(string code, string message) : Exception(message)
{
    /// <summary>Bad path, link, .git, deny list, closed session, not found.</summary>
    public const string Refused = "code.refused";

    /// <summary>The member has no clone (yet, or any more).</summary>
    public const string NoClone = "code.noClone";

    /// <summary>A parameter is missing or of the wrong type.</summary>
    public const string BadParams = "bad_params";

    public string Code { get; } = code;

    internal static CodeBrowserException Refuse(string message) => new(Refused, message);
}
