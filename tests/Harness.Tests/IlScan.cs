using System.Reflection;
using System.Reflection.Emit;

namespace Harness.Tests;

/// <summary>
/// Reading compiled IL: every method and constructor of an assembly, each instruction with the
/// method, field or string it names, and the method a lambda, local function or state machine
/// belongs to. The decoder and <see cref="Owner"/> are <see cref="PumpArchitectureTests"/>' own,
/// with string operands added, so a path written as a literal is seen where it is loaded, and with
/// top-level statements' nested names (<c>&lt;&lt;Main&gt;$&gt;b__0_1</c>) folded to <c>&lt;Main&gt;$</c>.
/// </summary>
internal static class IlScan
{
    public sealed record Instruction(int Offset, int Next, OpCode OpCode, MethodBase? Method, FieldInfo? Field, string? String);

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static IEnumerable<MethodBase> Methods(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all)) yield return method;
            foreach (var constructor in type.GetConstructors(all)) yield return constructor;
        }
    }

    /// <summary><c>Type.Method</c> for a method, a lambda or local function in it, or its state machine.</summary>
    public static string Owner(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name;

        // A state machine or a closure class: `<RunOneAsync>d__12`, `<>c__DisplayClass4_0`.
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            if (Inner(type.Name) is { Length: > 0 } inner) name = inner;
            type = type.DeclaringType!;
        }

        // A lambda or local function: `<Snapshot>b__40_0`, `<RunAsync>g__Local|3_0`, and in top-level
        // statements `<<Main>$>b__0_1`, which is `<Main>$`'s.
        if (name.StartsWith('<') && Inner(name) is { Length: > 0 } written) name = written;

        return $"{type.Name}.{name}";
    }

    /// <summary>What a compiler-generated name's first bracket pair holds, nested pairs included.</summary>
    private static string Inner(string generated)
    {
        var depth = 0;
        for (var i = 0; i < generated.Length; i++)
        {
            if (generated[i] == '<') depth++;
            else if (generated[i] == '>' && --depth == 0) return generated[1..i];
        }

        return "";
    }

    public static IReadOnlyList<Instruction> Decode(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il) return [];

        var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var list = new List<Instruction>();
        var i = 0;

        while (i < il.Length)
        {
            var offset = i;
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[++i]) : il[i];
            i += 1;
            var op = OpCodesByValue[value];

            MethodBase? target = null;
            FieldInfo? field = null;
            string? text = null;

            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };

            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    try { target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArguments, methodArguments); }
                    catch (ArgumentException) { }
                    break;
                case OperandType.InlineField:
                    try { field = method.Module.ResolveField(BitConverter.ToInt32(il, i), typeArguments, methodArguments); }
                    catch (ArgumentException) { }
                    break;

                // `ldstr`: without it, a rule about a path would pass on code it never saw.
                case OperandType.InlineString:
                    try { text = method.Module.ResolveString(BitConverter.ToInt32(il, i)); }
                    catch (ArgumentException) { }
                    break;
            }

            var next = i + size;
            list.Add(new Instruction(offset, next, op, target, field, text));
            i = next;
        }

        return list;
    }
}
