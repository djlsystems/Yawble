using System.Reflection;
using System.Reflection.Emit;
using Harness.Containers;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// P7 MADE STRUCTURAL (card 621 verification, F1; hardened for R2-F1). <see cref="PluginMemberEndToEndTests.P7_Nothing_plugin_specific_is_in_the_pump"/>
/// scans the words of Harness.Containers; this reads its COMPILED IL, so a kind check cannot hide
/// behind a name that does not say "plugin":
///
/// <list type="number">
/// <item><see cref="MemberRef"/> is called from exactly one place in the assembly - <c>KindOf</c>,
/// in <see cref="MemberRuntime.Snapshot"/>, which labels the snapshot and decides nothing.</item>
/// <item>A member's implementation is only ever PASSED ON, by an ALLOW-LIST. The value of
/// <see cref="ContainerDefinition.Agent"/>, of <see cref="ContainerSnapshot.Agent"/> and
/// <see cref="ContainerSnapshot.Kind"/>, and of <see cref="MemberRef.KindOf"/> is followed through
/// the stack, locals, fields, closures, state machines and return values, and the only things
/// allowed to consume it are: a record of Harness.Contracts that carries it (the snapshot, the
/// invocation the router receives, the definition), a delegate the Host handed in, <c>KindOf</c>
/// itself, and logging. Anything else - a comparison, a branch, a string method such as
/// <c>ToLowerInvariant</c> or <c>Split</c>, a tuple, a helper of the pump's own - is a violation,
/// so a transform in between cannot launder the value into a comparison.</item>
/// <item>The WHOLE <see cref="ContainerDefinition"/> carries the Agent too (round 2, P1), so it is
/// followed the same way - from any parameter, field or call result of that type - but by a
/// DENY-LIST, because a definition is legitimately handed everywhere: it may not reach anything that
/// reads it whole - <c>ToString</c>, <c>Equals</c>, <c>GetHashCode</c>, <c>PrintMembers</c>,
/// <c>Deconstruct</c>, the equality operators, string formatting or interpolation, JSON, reflection,
/// or logging. Nor may it be WIDENED past that list (part 2 round 2, P1-b): handed to a parameter not
/// declared as a <see cref="ContainerDefinition"/> - <c>object</c>, a generic <c>T</c>, a collection's
/// <c>Add</c> - or stored in an array, where BCL code or a helper reads it whole out of sight. The
/// hand-offs stay allowed: a record of Harness.Contracts (the invocation the router receives, the
/// snapshot, a <c>with</c>), and a delegate the Host handed in.</item>
/// </list>
///
/// Lambdas, local functions and async state machines are attributed to the method that wrote them,
/// so moving a check into a closure does not move it out of reach.
/// </summary>
public sealed class PumpArchitectureTests
{
    private static readonly Assembly Pump = typeof(MemberRuntime).Assembly;

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
    public void The_pump_only_passes_a_members_implementation_on()
    {
        var scan = ImplementationFlow.Scan(Methods(Pump));

        Assert.Empty(scan.Violations);
        Assert.Empty(ImplementationFlow.Scan(Methods(Pump), ImplementationFlow.Definitions).Violations);

        // The scan must be seeing the IL at all: the runtime reads Agent to hand it to the router
        // and the snapshot, and each of those is an allowed consumption.
        Assert.True(scan.Reads > 0, "no read of a member's implementation was found - the IL scan is not seeing the pump");
        Assert.Contains(scan.PassedTo, t => t.DeclaringType == typeof(MemberInvocation));
        Assert.Contains(scan.PassedTo, t => t.DeclaringType == typeof(ContainerSnapshot));
    }

    /// <summary>THE SCAN CATCHES WHAT IT IS MEANT TO FORBID: the plain comparison, and the three
    /// evasions the round 2 verification wrote past the previous "next call" rule.</summary>
    [Theory]
    [InlineData(nameof(ComparesAnAgent))]
    [InlineData(nameof(LowerCasesThenStartsWith))]
    [InlineData(nameof(SplitsThenCompares))]
    [InlineData(nameof(ComparesTheSnapshotsKind))]
    [InlineData(nameof(ComparesThroughALocalAndAHelper))]
    [InlineData(nameof(ReadsTheDefinitionsText))]
    [InlineData(nameof(ComparesTheDefinitionWithAClone))]
    [InlineData(nameof(ComparesTheDefinitionThroughObjectEquals))]
    [InlineData(nameof(ComparesTheDefinitionsHash))]
    [InlineData(nameof(ReadsTheDefinitionsMembersByReflection))]
    [InlineData(nameof(SerialisesTheDefinition))]
    [InlineData(nameof(InterpolatesTheDefinition))]
    [InlineData(nameof(DeconstructsTheDefinition))]
    [InlineData(nameof(ComparesTheDefinitionInsideAnArray))]
    [InlineData(nameof(ComparesTheDefinitionInsideAHashSet))]
    [InlineData(nameof(ReadsTheDefinitionThroughAnObjectParameter))]
    [InlineData(nameof(FormatsTheDefinitionThroughAFactory))]
    public void The_scan_catches_a_violation(string probe)
    {
        var method = typeof(PumpArchitectureTests).GetMethod(probe, BindingFlags.NonPublic | BindingFlags.Static)!;

        var violations = ImplementationFlow.Scan([method]).Violations
            .Concat(ImplementationFlow.Scan([method], ImplementationFlow.Definitions).Violations);

        Assert.NotEmpty(violations);
    }

    /// <summary>The record is sealed, so nothing outside it can call its private <c>PrintMembers</c>
    /// directly (a reflection call is <see cref="ReadsTheDefinitionsMembersByReflection"/>); the
    /// rule is pinned on the method itself.</summary>
    [Fact]
    public void The_definition_rules_forbid_PrintMembers()
    {
        var printMembers = typeof(ContainerDefinition).GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.False(ImplementationFlow.Definitions.PassesOn(printMembers, -1));
    }

    [Fact]
    public void The_scan_allows_passing_the_implementation_on()
    {
        var method = typeof(PumpArchitectureTests).GetMethod(nameof(PassesItOn), BindingFlags.NonPublic | BindingFlags.Static)!;

        var scan = ImplementationFlow.Scan([method]);

        Assert.Empty(scan.Violations);
        Assert.True(scan.Reads > 0);
        Assert.Empty(ImplementationFlow.Scan([method], ImplementationFlow.Definitions).Violations);
    }

    private static bool ComparesAnAgent(ContainerDefinition definition) => definition.Agent == "x";

    private static bool LowerCasesThenStartsWith(ContainerDefinition definition) =>
        definition.Agent.ToLowerInvariant().StartsWith("p" + "lugin:", StringComparison.Ordinal);

    private static bool SplitsThenCompares(ContainerDefinition definition) => definition.Agent.Split(':')[0] == "pl" + "ugin";

    private static bool ComparesTheSnapshotsKind(MemberRuntime member) => member.Snapshot().Kind?.ToString() == "pl" + "ugin";

    private static bool ComparesThroughALocalAndAHelper(ContainerDefinition definition)
    {
        var kept = definition.Agent;
        Console.Out.Flush();
        return Same(kept);
    }

    private static bool Same(string value) => value.Length == 3;

    // P1 (round 2): the WHOLE record read past the Agent's getter carries the kind with it.
    private static bool ReadsTheDefinitionsText(ContainerDefinition definition) =>
        definition.ToString().Contains("pl" + "ugin:", StringComparison.Ordinal);

    private static bool ComparesTheDefinitionWithAClone(ContainerDefinition definition) =>
        definition.Equals(definition with { Agent = "x" });

    private static bool ComparesTheDefinitionThroughObjectEquals(ContainerDefinition definition) =>
        Equals(definition, definition with { Agent = "x" });

    private static bool ComparesTheDefinitionsHash(ContainerDefinition definition) =>
        definition.GetHashCode() == (definition with { Agent = "x" }).GetHashCode();

    /// <summary>The record is sealed, so its <c>PrintMembers</c> is private and reachable only by
    /// reflection - which is what this does.</summary>
    private static object? ReadsTheDefinitionsMembersByReflection(ContainerDefinition definition) =>
        typeof(ContainerDefinition).GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(definition, [new System.Text.StringBuilder()]);

    private static bool SerialisesTheDefinition(ContainerDefinition definition) =>
        System.Text.Json.JsonSerializer.Serialize(definition).Contains("pl" + "ugin:", StringComparison.Ordinal);

    private static bool InterpolatesTheDefinition(ContainerDefinition definition) =>
        $"{definition}".Contains("pl" + "ugin:", StringComparison.Ordinal);

    private static bool DeconstructsTheDefinition(ContainerDefinition definition)
    {
        definition.Deconstruct(out _, out var agent, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        return agent.Length == 3;
    }

    // P1-b (part 2 verification, round 2): the whole record read inside BCL code, or behind a
    // parameter that is not typed as a definition, where the deny-list by callee never looked.
    private static bool ComparesTheDefinitionInsideAnArray(ContainerDefinition definition) =>
        Enumerable.Contains(new[] { definition }, definition with { Agent = "x" });

    private static bool ComparesTheDefinitionInsideAHashSet(ContainerDefinition definition) =>
        new HashSet<ContainerDefinition> { definition }.Contains(definition with { Agent = "x" });

    private static bool ReadsTheDefinitionThroughAnObjectParameter(ContainerDefinition definition) =>
        TextOf(definition).Contains("pl" + "ugin:", StringComparison.Ordinal);

    private static string TextOf(object value) => value.ToString()!;

    private static bool FormatsTheDefinitionThroughAFactory(ContainerDefinition definition) =>
        System.Runtime.CompilerServices.FormattableStringFactory.Create("{0}", definition).ToString()
            .Contains("pl" + "ugin:", StringComparison.Ordinal);

    private static ContainerDefinition PassesItOn(ContainerDefinition definition, ILogger log, Func<string, bool> watchable, string? agent)
    {
        log.LogInformation("member {Agent}", definition.Agent);
        _ = watchable(definition.Agent);
        return definition with { Agent = agent ?? definition.Agent };
    }

    private sealed record Call(string Owner, MethodBase Target);

    private static IEnumerable<Call> Calls() =>
        Methods(Pump).SelectMany(method => Il.Decode(method)
            .Where(i => i.Method is not null)
            .Select(i => new Call(Owner(method), i.Method!)));

    private static IEnumerable<MethodBase> Methods(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all)) yield return method;
            foreach (var constructor in type.GetConstructors(all)) yield return constructor;
        }
    }

    /// <summary>`MemberRuntime.Snapshot` for a method, a lambda in it, or its state machine.</summary>
    internal static string Owner(MethodBase method)
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

    /// <summary>
    /// WHERE A MEMBER'S IMPLEMENTATION GOES. An abstract interpretation of each method's IL over its
    /// control flow, with one bit per stack slot - "this value is, or was taken from, a member's
    /// implementation" - carried through locals and arguments (per method), and through fields and
    /// return values (across the whole scan, to a fixed point).
    /// </summary>
    private sealed class ImplementationFlow
    {
        /// <summary>What one scan follows: where the value comes from, and what may consume it.</summary>
        public sealed record Rules(
            string Noun,
            Func<MethodBase, bool> IsSource,
            Func<FieldInfo, bool> FieldIsSource,
            Func<ParameterInfo, bool> ParameterIsSource,
            Func<MethodBase, int, bool> PassesOn,
            bool DecidesInIl,
            bool MayStoreInAnArray);

        /// <summary>A member's implementation: the Agent, the snapshot's Agent and Kind, <c>KindOf</c>.
        /// An ALLOW-LIST of consumers, and any IL decision on it is a violation.</summary>
        public static readonly Rules Implementations = new(
            "implementation", target => Sources.Contains(target), _ => false, _ => false, (target, _) => PassesOn(target),
            DecidesInIl: true, MayStoreInAnArray: true);

        /// <summary>A whole <see cref="ContainerDefinition"/>. A DENY-LIST of consumers that read it
        /// whole, and no widening past it (P1-b); IL on it (a null check) decides nothing about its kind.</summary>
        public static readonly Rules Definitions = new(
            "definition",
            target => target is MethodInfo { ReturnType: var type } && type == typeof(ContainerDefinition),
            field => field.FieldType == typeof(ContainerDefinition),
            parameter => parameter.ParameterType == typeof(ContainerDefinition),
            AcceptsADefinition,
            DecidesInIl: false,
            MayStoreInAnArray: false);

        /// <summary>Whether a definition may be handed to <paramref name="target"/> at
        /// <paramref name="slot"/> (its parameter's position, or -1 for <c>this</c>).</summary>
        private static bool AcceptsADefinition(MethodBase target, int slot)
        {
            if (ReadsADefinitionWhole(target)) return false;

            var type = target.DeclaringType;

            // The hand-offs: a record of the contracts carrying it on (the invocation the router
            // receives, the snapshot, a `with`), and a delegate the Host handed the pump.
            if (type?.Assembly == typeof(ContainerDefinition).Assembly && type != typeof(MemberRef)
                && (target is ConstructorInfo || target.Name.StartsWith("set_", StringComparison.Ordinal)))
            {
                return true;
            }

            if (type is not null && typeof(Delegate).IsAssignableFrom(type) && target.Name == nameof(Action.Invoke)) return true;

            // Its own members, called on it.
            if (slot < 0) return type == typeof(ContainerDefinition);

            // Anywhere else only as a definition, and as DECLARED: `HashSet<ContainerDefinition>.Add`
            // takes a `T`, which reads it whole through Equals and GetHashCode.
            var declared = Declared(target).GetParameters()[slot].ParameterType;
            if (declared.IsByRef) declared = declared.GetElementType()!;

            return declared == typeof(ContainerDefinition);
        }

        /// <summary>The method as written - on its open generic type, before its type arguments.</summary>
        private static MethodBase Declared(MethodBase target)
        {
            try { return target.Module.ResolveMethod(target.MetadataToken) ?? target; }
            catch (ArgumentException) { return target; }
        }

        private static readonly HashSet<string> WholeReads =
        [
            nameof(ToString), nameof(Equals), nameof(GetHashCode), "PrintMembers", "Deconstruct",
            nameof(ReferenceEquals), "op_Equality", "op_Inequality",
        ];

        private static bool ReadsADefinitionWhole(MethodBase target)
        {
            var type = target.DeclaringType;
            var space = type?.Namespace ?? "";

            return WholeReads.Contains(target.Name)
                || space.StartsWith("System.Text.Json", StringComparison.Ordinal)
                || space.StartsWith("System.Reflection", StringComparison.Ordinal)
                || type == typeof(System.Text.StringBuilder)
                || type == typeof(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler)
                || (type == typeof(string) && target.Name is nameof(string.Concat) or nameof(string.Format) or nameof(string.Join))
                || type == typeof(LoggerExtensions) || type == typeof(ILogger);
        }

        /// <summary>Where the value comes from.</summary>
        private static readonly MethodBase[] Sources =
        [
            typeof(ContainerDefinition).GetProperty(nameof(ContainerDefinition.Agent))!.GetMethod!,
            typeof(ContainerSnapshot).GetProperty(nameof(ContainerSnapshot.Agent))!.GetMethod!,
            typeof(ContainerSnapshot).GetProperty(nameof(ContainerSnapshot.Kind))!.GetMethod!,
            typeof(MemberRef).GetMethod(nameof(MemberRef.KindOf))!,
        ];

        /// <summary>Whether <paramref name="target"/> may be handed the value. Its result is the
        /// value again only for <c>KindOf</c>, which is a source.</summary>
        private static bool PassesOn(MethodBase target)
        {
            var type = target.DeclaringType;

            // A record of the contracts carrying it on: the snapshot, the invocation the router is
            // handed, the definition (a `with` is its clone and an init setter).
            if (type?.Assembly == typeof(ContainerDefinition).Assembly && type != typeof(MemberRef)
                && (target is ConstructorInfo || target.Name.StartsWith("set_", StringComparison.Ordinal)))
            {
                return true;
            }

            if (target == Sources[3]) return true;

            // A delegate the Host handed the pump - the decision is the Host's.
            if (type is not null && typeof(Delegate).IsAssignableFrom(type) && target.Name == nameof(Action.Invoke)) return true;

            // Logging.
            return type == typeof(LoggerExtensions) || type == typeof(ILogger);
        }

        private readonly Rules _rules;

        public int Reads { get; private set; }

        public List<string> Violations { get; } = [];

        public HashSet<MethodBase> PassedTo { get; } = [];

        private readonly HashSet<(Guid, int)> _fields;
        private readonly HashSet<(Guid, int)> _returning;

        private ImplementationFlow(Rules rules, IEnumerable<(Guid, int)> fields, IEnumerable<(Guid, int)> returning)
        {
            _rules = rules;
            _fields = [.. fields];
            _returning = [.. returning];
        }

        private static (Guid, int) Key(MemberInfo member) => (member.Module.ModuleVersionId, member.MetadataToken);

        public static ImplementationFlow Scan(IEnumerable<MethodBase> methods, Rules? rules = null)
        {
            rules ??= Implementations;
            var list = methods.Where(m => m.GetMethodBody() is not null).ToList();
            var decoded = list.ToDictionary(m => m, Il.Decode);
            var flow = new ImplementationFlow(rules, [], []);

            // FIXED POINT: a field written with the value, or a method returning it, is a source in
            // every method, including those already walked - so walk again until nothing is learned.
            while (true)
            {
                var next = new ImplementationFlow(rules, flow._fields, flow._returning);

                foreach (var method in list) next.Interpret(method, decoded[method]);

                if (next._fields.SetEquals(flow._fields) && next._returning.SetEquals(flow._returning)) return next;

                flow = next;
            }
        }

        private bool IsSource(MethodBase target) =>
            _rules.IsSource(target) || _returning.Contains(Key(target));

        /// <summary>`ldarg n` of a parameter the rules name as a source (never `this`: the pump
        /// declares none of the contracts' records).</summary>
        private bool ArgumentIsSource(MethodBase method, int index)
        {
            var position = method.IsStatic ? index : index - 1;
            var parameters = method.GetParameters();
            return position >= 0 && position < parameters.Length && _rules.ParameterIsSource(parameters[position]);
        }

        private void Violation(MethodBase method, string what)
        {
            var line = $"{Owner(method)} {what}";
            if (!Violations.Contains(line)) Violations.Add(line);
        }

        private void Interpret(MethodBase method, IReadOnlyList<Il.Instruction> code)
        {
            var at = new Dictionary<int, int>();
            for (var i = 0; i < code.Count; i++) at[code[i].Offset] = i;

            var body = method.GetMethodBody()!;
            var locals = new HashSet<int>();
            var args = new HashSet<int>();

            // Locals and arguments are FLOW-INSENSITIVE: once holding the value, always. Repeat the
            // walk until that set stops growing.
            while (true)
            {
                var (localsBefore, argsBefore) = (locals.Count, args.Count);
                var entry = new Dictionary<int, bool[]> { [0] = [] };

                foreach (var clause in body.ExceptionHandlingClauses)
                {
                    entry[clause.HandlerOffset] = clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter ? [false] : [];
                    if (clause.Flags == ExceptionHandlingClauseOptions.Filter) entry[clause.FilterOffset] = [false];
                }

                var states = new Dictionary<int, bool[]>();
                var work = new Stack<int>();

                foreach (var (offset, stack) in entry)
                {
                    states[at[offset]] = stack;
                    work.Push(at[offset]);
                }

                while (work.Count > 0)
                {
                    var index = work.Pop();
                    var stack = new List<bool>(states[index]);
                    var instruction = code[index];

                    foreach (var next in Step(method, instruction, stack, locals, args, at))
                    {
                        if (next >= code.Count) continue;

                        if (!states.TryGetValue(next, out var known))
                        {
                            states[next] = [.. stack];
                            work.Push(next);
                        }
                        else
                        {
                            if (known.Length != stack.Count)
                            {
                                throw new InvalidOperationException($"{Owner(method)}: stack depth differs at IL_{code[next].Offset:x4}.");
                            }

                            var merged = known.Zip(stack, (a, b) => a || b).ToArray();
                            if (!merged.SequenceEqual(known))
                            {
                                states[next] = merged;
                                work.Push(next);
                            }
                        }
                    }
                }

                if (locals.Count == localsBefore && args.Count == argsBefore) return;
            }
        }

        /// <summary>One instruction's effect on the stack, and the instructions that can follow it.</summary>
        private IEnumerable<int> Step(
            MethodBase method, Il.Instruction instruction, List<bool> stack,
            HashSet<int> locals, HashSet<int> args, Dictionary<int, int> at)
        {
            var op = instruction.OpCode;
            var name = op.Name!;

            bool Pop()
            {
                var value = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return value;
            }

            void Push(bool value) => stack.Add(value);

            if (name.StartsWith("ldloca", StringComparison.Ordinal))
            {
                Push(locals.Contains(instruction.Index));
            }
            else if (name.StartsWith("ldloc", StringComparison.Ordinal))
            {
                Push(locals.Contains(instruction.Index));
            }
            else if (name.StartsWith("stloc", StringComparison.Ordinal))
            {
                if (Pop()) locals.Add(instruction.Index);
            }
            else if (name.StartsWith("ldarg", StringComparison.Ordinal))
            {
                Push(args.Contains(instruction.Index) || ArgumentIsSource(method, instruction.Index));
            }
            else if (name.StartsWith("starg", StringComparison.Ordinal))
            {
                if (Pop()) args.Add(instruction.Index);
            }
            else if (instruction.Field is { } field && name is "ldfld" or "ldflda" or "ldsfld" or "ldsflda")
            {
                if (name.StartsWith("ldfld", StringComparison.Ordinal)) Pop();
                Push(_fields.Contains(Key(field)) || _rules.FieldIsSource(field));
            }
            else if (instruction.Field is { } stored && name is "stfld" or "stsfld")
            {
                if (Pop()) _fields.Add(Key(stored));
                if (name == "stfld") Pop();
            }
            else if (name == "dup")
            {
                var top = stack[^1];
                Push(top);
            }
            else if (name == "ret")
            {
                if (method is MethodInfo { ReturnType: var returns } && returns != typeof(void) && Pop())
                {
                    _returning.Add(Key(method));
                }
            }
            else if (instruction.Method is { } target && name is "call" or "callvirt" or "newobj")
            {
                var parameters = target.GetParameters().Length;
                var popped = parameters + (target.IsStatic || name == "newobj" ? 0 : 1);
                var carried = false;
                var refused = false;

                // Popped last argument first; the last pop of an instance call is `this` (slot -1).
                for (var i = 0; i < popped; i++)
                {
                    if (!Pop()) continue;
                    carried = true;
                    if (!_rules.PassesOn(target, parameters - 1 - i)) refused = true;
                }

                if (target == Sources[0] || target == Sources[1] || target == Sources[2]) Reads += 1;

                if (carried)
                {
                    if (!refused) PassedTo.Add(target);
                    else Violation(method, $"hands a member's {_rules.Noun} to {target.DeclaringType?.Name}.{target.Name}.");
                }

                var returns = name == "newobj" || (target is MethodInfo info && info.ReturnType != typeof(void));
                if (returns) Push(IsSource(target));
            }
            else if (name is "calli" or "jmp")
            {
                throw new NotSupportedException($"{Owner(method)}: {name} is not interpreted.");
            }
            else
            {
                var pops = PopCount(op);
                var carried = false;
                for (var i = 0; i < pops; i++) carried |= Pop();

                var passesThrough = name is "castclass" or "isinst" or "box" or "unbox.any" || name.StartsWith("ldelem", StringComparison.Ordinal);

                if (carried && name.StartsWith("stelem", StringComparison.Ordinal) && !_rules.MayStoreInAnArray)
                {
                    // An array hands it to whatever reads the array - `Enumerable.Contains`, a
                    // `params object[]` formatter - out of this scan's sight.
                    Violation(method, $"stores a member's {_rules.Noun} in an array.");
                }
                else if (carried && name.StartsWith("stelem", StringComparison.Ordinal) && stack.Count > 0)
                {
                    // `newarr; dup; ldc; <value>; stelem` - an argument array. The copy `dup` left
                    // below is the array, and it carries the value from here on.
                    stack[^1] = true;
                }
                else if (carried && !passesThrough && name != "pop" && _rules.DecidesInIl)
                {
                    // Deciding on it (a branch, `ceq`), or storing it where this cannot follow.
                    Violation(method, $"uses a member's {_rules.Noun} in `{name}`.");
                }

                for (var i = 0; i < PushCount(op); i++) Push(carried && passesThrough);
            }

            // Where control goes next.
            if (name is "leave" or "leave.s") stack.Clear();

            return op.FlowControl switch
            {
                FlowControl.Return or FlowControl.Throw => [],
                FlowControl.Branch => instruction.Targets.Select(t => at[t]),
                FlowControl.Cond_Branch => instruction.Targets.Select(t => at[t]).Append(at.GetValueOrDefault(instruction.Next, int.MaxValue)),
                _ when name is "endfinally" or "endfilter" => [],
                _ => [at.GetValueOrDefault(instruction.Next, int.MaxValue)],
            };
        }

        private static int PopCount(OpCode op) => op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Varpop => throw new NotSupportedException(op.Name),
            var behaviour => behaviour.ToString().Split('_').Length,
        };

        private static int PushCount(OpCode op) => op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush => throw new NotSupportedException(op.Name),
            _ => 1,
        };
    }

    /// <summary>A minimal IL decoder: each instruction with the operand this scan needs.</summary>
    private static class Il
    {
        public sealed record Instruction(
            int Offset, int Next, OpCode OpCode, MethodBase? Method, FieldInfo? Field, int Index, IReadOnlyList<int> Targets);

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

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
                var index = ImplicitIndex(op);
                var targets = new List<int>();

                var size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                    _ => 4,
                };

                var next = i + size;

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
                    case OperandType.ShortInlineVar:
                        index = il[i];
                        break;
                    case OperandType.InlineVar:
                        index = BitConverter.ToUInt16(il, i);
                        break;
                    case OperandType.ShortInlineBrTarget:
                        targets.Add(next + (sbyte)il[i]);
                        break;
                    case OperandType.InlineBrTarget:
                        targets.Add(next + BitConverter.ToInt32(il, i));
                        break;
                    case OperandType.InlineSwitch:
                        var count = BitConverter.ToInt32(il, i);
                        for (var k = 0; k < count; k++) targets.Add(next + BitConverter.ToInt32(il, i + 4 + (4 * k)));
                        break;
                }

                list.Add(new Instruction(offset, next, op, target, field, index, targets));
                i = next;
            }

            return list;
        }

        /// <summary>The index of `ldloc.2`, `stloc.0`, `ldarg.3` and their kin, which carry no operand.</summary>
        private static int ImplicitIndex(OpCode op) =>
            op.Name is { } name && name.Length > 2 && name[^2] == '.' && char.IsAsciiDigit(name[^1])
                && (name.StartsWith("ldloc", StringComparison.Ordinal) || name.StartsWith("stloc", StringComparison.Ordinal)
                    || name.StartsWith("ldarg", StringComparison.Ordinal))
                ? name[^1] - '0'
                : -1;
    }
}
