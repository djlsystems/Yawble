using System.Reflection;
using Harness.Host;
using ModelContextProtocol.Server;

namespace Harness.Tests;

/// <summary>
/// An optional MCP tool parameter must carry a default. Without one the tool marshaller treats it
/// as required, so a call that leaves it out fails with "missing a value for the required parameter"
/// (for example, `backlog` refusing a plain `list`).
/// </summary>
public sealed class McpToolParameterTests
{
    public static TheoryData<string> Tools()
    {
        var data = new TheoryData<string>();
        foreach (var method in ToolMethods()) data.Add($"{method.DeclaringType!.Name}.{method.Name}");
        return data;
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_optional_parameter_has_a_default(string tool)
    {
        var method = ToolMethods().Single(m => $"{m.DeclaringType!.Name}.{m.Name}" == tool);
        var nullability = new NullabilityInfoContext();

        var missing = method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Where(p => Nullable.GetUnderlyingType(p.ParameterType) is not null
                || nullability.Create(p).WriteState is NullabilityState.Nullable)
            .Where(p => !p.HasDefaultValue)
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(missing);
    }

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(PlatformMcpTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
