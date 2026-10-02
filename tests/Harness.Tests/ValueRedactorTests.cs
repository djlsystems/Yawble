using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The one way a set of secret values is replaced in text the Host stores or serves, shared by
/// plugin runs and agent runs: longest first, in any case, in its JSON-escaped forms, with a fixed
/// marker, and nothing shorter than <see cref="ValueRedactor.MinimumLength"/>.
/// </summary>
public sealed class ValueRedactorTests
{
    private const string Marker = DiagnosticRedaction.Placeholder;

    [Fact]
    public void The_longest_form_is_replaced_first_so_a_value_that_prefixes_another_leaves_no_tail()
    {
        var redactor = ValueRedactor.For(["abcd1234", "abcd1234efgh"]);

        Assert.Equal($"a {Marker} b {Marker}", redactor.Apply("a abcd1234efgh b abcd1234"));
        Assert.Equal("abcd1234efgh", redactor.Forms[0]);
    }

    [Fact]
    public void Each_value_is_replaced_in_any_case_and_in_its_json_escaped_forms()
    {
        const string Value = "fake-value+Qm7vX2pL/9tRw";
        var redactor = ValueRedactor.For([Value]);

        var plain = Value;
        var upper = Value.ToUpperInvariant();
        var escaped = JsonSerializer.Serialize(Value)[1..^1];
        var slashed = Value.Replace("/", "\\/", StringComparison.Ordinal);

        Assert.Contains("\\u002B", escaped, StringComparison.Ordinal);
        foreach (var form in new[] { plain, upper, escaped, slashed, escaped.ToLowerInvariant() })
        {
            Assert.Equal($"[{Marker}]", redactor.Apply($"[{form}]"));
        }
    }

    [Fact]
    public void A_value_shorter_than_four_characters_is_not_redacted()
    {
        Assert.Equal(4, ValueRedactor.MinimumLength);

        Assert.Equal("abc and abc", ValueRedactor.For(["abc"]).Apply("abc and abc"));
        Assert.Same(ValueRedactor.Empty, ValueRedactor.For(["abc", "", null]));
        Assert.Equal($"{Marker} and {Marker}", ValueRedactor.For(["abcd"]).Apply("abcd and ABCD"));
    }

    [Fact]
    public void With_nothing_to_redact_the_same_text_is_returned()
    {
        // Text the credential-shape rules would rewrite, and the marker's own word.
        var text = "password=hunter2 " + new string('x', 20) + "Ab9_-Zq" + " redacted [redacted]";

        Assert.Same(text, ValueRedactor.Empty.Apply(text));
        Assert.Same(text, ValueRedactor.For(["fake-not-present-Kd8w"]).Apply(text));
        Assert.Null(ValueRedactor.Empty.ApplyOrNull(null));
    }

    [Fact]
    public void Two_sets_together_redact_what_either_would()
    {
        var both = ValueRedactor.For(["fake-first-Hy6t"]).With(ValueRedactor.For(["fake-second-Pz3q"]));

        Assert.Equal($"{Marker} {Marker}", both.Apply("fake-first-Hy6t FAKE-SECOND-PZ3Q"));
        Assert.Same(ValueRedactor.Empty, ValueRedactor.Empty.With(ValueRedactor.Empty));
    }
}
