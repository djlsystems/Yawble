using System.Collections.Concurrent;
using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Harness.Tests.Host;

/// <summary>
/// The console writes JSON with scopes, and a signed-in request's lines carry its
/// principal id on the scope.
/// </summary>
public sealed class JsonLoggingTests : IAsyncLifetime
{
    private const string Email = "logger@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-logging-test-{Guid.NewGuid():N}");

    private readonly ScopeCapture _capture = new();

    private WebApplicationFactory<Program> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Routing.EndpointMiddleware", "Information")
                .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_capture)));

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
    }

    [Fact]
    public void The_console_formatter_is_json_and_writes_scopes()
    {
        var console = _factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>();
        var json = _factory.Services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>();

        Assert.Equal(ConsoleFormatterNames.Json, console.CurrentValue.FormatterName);
        Assert.True(json.CurrentValue.IncludeScopes);
    }

    [Fact]
    public async Task A_signed_in_requests_lines_carry_its_principal_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();

        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, ct))
            .EnsureSuccessStatusCode();

        var me = await client.GetFromJsonAsync<Me>("/api/auth/me", ct);
        Assert.NotNull(me?.Id);

        Assert.Contains(
            _capture.Lines,
            line => line.Message.Contains("/api/auth/me", StringComparison.Ordinal)
                && line.Scopes.TryGetValue(PrincipalLogScope.Key, out var id)
                && Equals(id, me!.Id));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Me(string Id);

    private sealed record Line(string Message, IReadOnlyDictionary<string, object?> Scopes);

    /// <summary>A provider that records each line with the scope values in force when it was written.</summary>
    private sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<Line> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose() { }

        private sealed class Logger(ScopeCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopes = new Dictionary<string, object?>();

                owner._scopes.ForEachScope(
                    (scope, into) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        {
                            foreach (var (key, value) in pairs) into[key] = value;
                        }
                    },
                    scopes);

                owner.Lines.Enqueue(new Line(formatter(state, exception), scopes));
            }
        }
    }
}
