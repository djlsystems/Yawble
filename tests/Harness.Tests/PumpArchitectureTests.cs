using System.Reflection;
using System.Reflection.Emit;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// P7 MADE STRUCTURAL (card 621 verification, F1). <see cref="PluginMemberEndToEndTests.P7_Nothing_plugin_specific_is_in_the_pump"/>
/// scans the words of Harness.Containers; this reads its COMPILED IL, so a kind check cannot hide
/// behind a name that does not say "plugin":
///
/// <list type="number">
/// <item><see cref="MemberRef"/> is called from exactly one place in the assembly - <c>KindOf</c>,
/// in <see cref="MemberRuntime.Snapshot"/>, which labels the snapshot and decides nothing.</item>
/// <item>A value read from <see cref="ContainerDefinition.Agent"/> is never handed to a comparison:
/// the runtime and the pump pass a member's implementation along and never branch on it.</item>
/// </list>
///
/// Lambdas, local functions and async state machines are attributed to the method that wrote them,
/// so moving a check into a closure does not move it out of reach.
/// </summary>
public sealed class PumpArchitectureTests
{
    private static readonly Assembly Pump = typeof(MemberRuntime).Assembly;

    /// <summary>What a comparison on a string compiles to - `==`, `is "x"`, a `switch` (which hashes
    /// first), and the ordinal and prefix helpers.</summary>
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal)
    {
        "op_Equality", "op_Inequality", "Equals", "Compare", "CompareOrdinal", "CompareTo",
        "StartsWith", "EndsWith", "Contains", "IndexOf", "GetHashCode", "ComputeStringHash",
        "SequenceEqual",
    };

    [Fact]
    public void MemberRef_is_used_in_the_pump_only_by_KindOf_in_Snapshot()
    {
        var uses = Calls()
            .Where(c => c.Target.DeclaringType == typeof(MemberRef))
            .Select(c => $"{c.Owner}: {c.Target.Name}")
            .Distinct()
            .ToList();

        Assert.Equal([$"{nameof(MemberRuntime)}.{nameof(MemberRuntime.Snapshot)}: {nameof(MemberRef.KindOf)}"], uses);
    }

    [Fact]
    public void The_pump_never_compares_a_members_Agent()
    {
        var getter = typeof(ContainerDefinition).GetProperty(nameof(ContainerDefinition.Agent))!.GetMethod!;
        var reads = 0;

        foreach (var method in Methods())
        {
            var calls = CallsIn(method).ToList();

            for (var i = 0; i < calls.Count; i++)
            {
                if (calls[i] != getter) continue;
                reads += 1;

                // THE NEXT CALL IS WHAT CONSUMES THE VALUE: a load of a literal or a local between
                // them is not a call, so `definition.Agent == "x"` is get_Agent then op_Equality.
                if (i + 1 < calls.Count && Comparisons.Contains(calls[i + 1].Name))
                {
                    Assert.Fail($"{Owner(method)} compares ContainerDefinition.Agent with {calls[i + 1].DeclaringType?.Name}.{calls[i + 1].Name}.");
                }
            }
        }

        // The scan must be seeing the IL at all: the runtime reads Agent to pass it on.
        Assert.True(reads > 0, "no read of ContainerDefinition.Agent was found - the IL scan is not seeing the pump");
    }

    [Fact]
    public void The_scan_catches_a_comparison_it_is_meant_to_forbid()
    {
        var calls = CallsIn(typeof(PumpArchitectureTests).GetMethod(nameof(ComparesAnAgent), BindingFlags.NonPublic | BindingFlags.Static)!).ToList();
        var getter = typeof(ContainerDefinition).GetProperty(nameof(ContainerDefinition.Agent))!.GetMethod!;

        var at = calls.IndexOf(getter);
        Assert.True(at >= 0 && Comparisons.Contains(calls[at + 1].Name));
    }

    private static bool ComparesAnAgent(ContainerDefinition definition) => definition.Agent == "x";

    private sealed record Call(string Owner, MethodBase Target);

    private static IEnumerable<Call> Calls() =>
        Methods().SelectMany(method => CallsIn(method).Select(target => new Call(Owner(method), target)));

    private static IEnumerable<MethodBase> Methods()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in Pump.GetTypes())
        {
            foreach (var method in type.GetMethods(all)) yield return method;
            foreach (var constructor in type.GetConstructors(all)) yield return constructor;
        }
    }

    /// <summary>`MemberRuntime.Snapshot` for a method, a lambda in it, or its state machine.</summary>
    private static string Owner(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name;

        // A state machine or a closure class: `<RunOneAsync>d__12`, `<>c__DisplayClass4_0`.
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            if (type.Name.IndexOf('>') is > 1 and var end) name = type.Name[1..end];
            type = type.DeclaringType!;
        }

        // A lambda or local function: `<Snapshot>b__40_0`, `<RunAsync>g__Local|3_0`.
        if (name.StartsWith('<') && name.IndexOf('>') is > 1 and var close) name = name[1..close];

        return $"{type.Name}.{name}";
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Every method a body calls, loads or constructs, in IL order.</summary>
    private static IEnumerable<MethodBase> CallsIn(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il) yield break;

        var i = 0;

        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[++i]) : il[i];
            i += 1;
            var op = OpCodesByValue[value];

            if (op.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, i);
                MethodBase? target = null;

                try
                {
                    target = method.Module.ResolveMethod(token,
                        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (target is not null) yield return target;
            }

            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
    }
}
