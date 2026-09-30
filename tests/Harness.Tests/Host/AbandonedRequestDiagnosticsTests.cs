using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A request its browser abandoned is not an unhandled exception. Two "Unhandled exception, 500,
/// TaskCanceledException" rows on /tokens and /workflows were fetches a team switch cancelled: an
/// answer nobody received, filed as the diagnostics store's loudest kind.
/// </summary>
public sealed class AbandonedRequestDiagnosticsTests
{
    [Fact]
    public async Task A_cancellation_after_the_client_left_writes_no_row_and_is_still_rethrown()
    {
        var log = new RecordingLog();
        using var aborted = new CancellationTokenSource();
        var context = ContextFor(log, aborted.Token);

        var pipeline = PipelineThrowing(() =>
        {
            aborted.Cancel();
            return new TaskCanceledException();
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline(context));
        Assert.Empty(log.Kinds);
    }

    [Fact]
    public async Task A_cancellation_with_the_client_still_there_is_still_recorded()
    {
        var log = new RecordingLog();
        var context = ContextFor(log, CancellationToken.None);

        var pipeline = PipelineThrowing(() => new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline(context));
        Assert.Equal([DiagnosticKinds.HttpUnhandledException], log.Kinds);
    }

    [Fact]
    public async Task Another_exception_after_the_client_left_is_still_recorded()
    {
        var log = new RecordingLog();
        using var aborted = new CancellationTokenSource();
        var context = ContextFor(log, aborted.Token);

        var pipeline = PipelineThrowing(() =>
        {
            aborted.Cancel();
            return new InvalidOperationException("broken");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(context));
        Assert.Equal([DiagnosticKinds.HttpUnhandledException], log.Kinds);
    }

    private static RequestDelegate PipelineThrowing(Func<Exception> failure)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        DiagnosticsMiddleware.Use(app);
        app.Run(_ => throw failure());
        return app.Build();
    }

    private static DefaultHttpContext ContextFor(IDiagnosticsLog log, CancellationToken aborted)
    {
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddSingleton<DiagnosticsRecorder>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = aborted });
        context.Request.Method = "GET";
        context.Request.Path = "/api/teams/t/tokens";
        return context;
    }

    private sealed class RecordingLog : IDiagnosticsLog
    {
        public List<string> Kinds { get; } = [];

        public Task WriteAsync(
            DiagnosticSeverity severity,
            string kind,
            string? source = null,
            string? route = null,
            int? status = null,
            string? exceptionType = null,
            string? message = null,
            string? detail = null,
            CancellationToken ct = default)
        {
            Kinds.Add(kind);
            return Task.CompletedTask;
        }

        public Task<DiagnosticsPage> ReadAsync(DiagnosticsFilter? filter = null, long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool?> HasAnyAsync(CancellationToken ct = default) => Task.FromResult<bool?>(Kinds.Count > 0);

        public Task TrimAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
