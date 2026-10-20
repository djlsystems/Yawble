using System.Collections.Concurrent;
using System.Reflection;

namespace Harness.Tests;

/// <summary>
/// A real store behind a counting proxy of one of its interfaces: how many times each method was
/// called, and a way to make the next call of one method fail. For a test that pins how often a
/// component reads or writes, not what it reads.
/// </summary>
public class CountingProxy : DispatchProxy
{
    private object _inner = null!;

    public ConcurrentDictionary<string, int> Calls { get; } = new();

    /// <summary>The method whose next call throws <see cref="IOException"/>, then clears itself.</summary>
    public string? FailNext { get; set; }

    public static TInterface Over<TInterface>(TInterface inner) where TInterface : class
    {
        var proxy = Create<TInterface, CountingProxy>();
        ((CountingProxy)(object)proxy)._inner = inner;
        return proxy;
    }

    public static CountingProxy Of(object proxy) => (CountingProxy)proxy;

    public int CallsTo(string method) => Calls.GetValueOrDefault(method);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.AddOrUpdate(name, 1, (_, n) => n + 1);
        if (FailNext == name)
        {
            FailNext = null;
            throw new IOException($"{name} failed on purpose");
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}
