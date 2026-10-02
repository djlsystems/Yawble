using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// THE WORKER CONNECTION'S DOOR: <c>GET /api/workers/connect</c>, where a worker process opens its one
/// WebSocket to control with the worker key. Anonymous at the policy level so each refusal can say
/// what is wrong in a sentence: no key or the wrong one, a credential that is not the worker key, or a
/// request that is not a WebSocket.
/// </summary>
public static class WorkerEndpoints
{
    /// <summary>No worker key, or not this instance's.</summary>
    public const string WrongKeyText = WorkerSentences.WrongKey;

    /// <summary>A person, or a credential that is not the worker key.</summary>
    public const string NotAWorkerText = WorkerSentences.NotAWorker;

    /// <summary>The worker key, but not on a WebSocket.</summary>
    public const string NotAWebSocketText = WorkerSentences.NotAWebSocket;

    /// <param name="accept">Takes a worker's WebSocket once the request is the worker key's, until the connection ends.</param>
    public static void Map(WebApplication app, Func<HttpContext, Task>? accept = null)
    {
        app.MapGet(WorkerKeyGate.ConnectRoute, async context =>
            {
                var principal = PrincipalClaims.From(context.User);

                if (principal is null)
                {
                    await Refuse(context, StatusCodes.Status401Unauthorized, WrongKeyText);
                    return;
                }

                if (principal.Kind != PrincipalKind.Worker)
                {
                    await Refuse(context, StatusCodes.Status403Forbidden, NotAWorkerText);
                    return;
                }

                if (!context.WebSockets.IsWebSocketRequest || accept is null)
                {
                    await Refuse(context, StatusCodes.Status400BadRequest, NotAWebSocketText);
                    return;
                }

                await accept(context);
            })
            .AllowAnonymous()
            .NoPermitRequired()
            .ExcludeFromDescription();
    }

    private static async Task Refuse(HttpContext context, int status, string sentence)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = sentence }, context.RequestAborted);
    }
}
