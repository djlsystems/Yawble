using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>A client of one MCP server, whatever its transport: its tools, calling one, and the last thing it said on its error stream.</summary>
public interface IMcpClient : IAsyncDisposable
{
    Task<JsonArray> ListToolsAsync(CancellationToken ct);
    Task<(string Text, bool IsError)> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct);
    string LastError();
}
