namespace Yawble.Core.Scripts.Language;

/// <summary>
/// The <c>while</c> step (design section 5.3): runs <c>body</c> while <c>when</c> holds, checked before each pass, then goes to
/// <c>then</c>; at most <c>max</c> passes (default <see cref="DefaultMax"/>, at most <see cref="MaxLimit"/>).
/// </summary>
public static class ScriptLoop
{
    public const string StepType = "while";
    public const int DefaultMax = 100;
    public const int MaxLimit = 10_000;

    /// <summary>Whether a step is a loop with a body (forEach or while): its body runs inside it.</summary>
    public static bool IsLoop(string type) => type is "forEach" or StepType;

    /// <summary>The reason a run fails when a while's condition still holds after <paramref name="max"/> passes.</summary>
    public static string RanMax(string stepId, int max) => $"the loop '{stepId}' ran {max} times (its max) and its condition still holds";
}
