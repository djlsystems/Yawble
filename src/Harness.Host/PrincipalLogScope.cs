using System.Security.Claims;

namespace Harness.Host;

/// <summary>
/// Puts the caller's principal id on the logging scope for the rest of the request, so
/// every line logged while serving it - the host's own and ASP.NET's - says who asked. The JSON
/// console writes it under <c>Scopes</c> as <c>PrincipalId</c>.
///
/// <para>
/// THE ID ONLY, never an email or a key: the host log is a file anyone who can read the volume can
/// read. An anonymous request carries no scope rather than a placeholder, so "no PrincipalId" means
/// "nobody signed in" and nothing else.
/// </para>
/// </summary>
public static class PrincipalLogScope
{
    public const string Key = "PrincipalId";

    public static void Use(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PrincipalLogScope));

        app.Use(async (context, next) =>
        {
            if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } id)
            {
                await next();
                return;
            }

            using (log.BeginScope(new Dictionary<string, object> { [Key] = id }))
            {
                await next();
            }
        });
    }
}
