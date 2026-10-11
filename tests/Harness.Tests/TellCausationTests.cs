using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

public sealed class TellCausationTests
{
    [Fact]
    public void A_container_that_names_no_causation_dispatches_inside_its_run()
    {
        Assert.Equal("547", TellCausation.Resolve(null, PrincipalKind.Container, 547));
        Assert.Equal("547", TellCausation.Resolve("  ", PrincipalKind.Container, 547));
    }

    [Fact]
    public void A_named_causation_is_kept()
    {
        Assert.Equal("12", TellCausation.Resolve("12", PrincipalKind.Container, 547));
    }

    [Theory]
    [InlineData(PrincipalKind.User)]
    [InlineData(PrincipalKind.TenantConcierge)]
    public void A_person_or_concierge_that_names_none_starts_a_workflow(PrincipalKind caller)
    {
        Assert.Null(TellCausation.Resolve(null, caller, 547));
    }

    [Fact]
    public void A_container_outside_any_run_has_nothing_to_inherit()
    {
        Assert.Null(TellCausation.Resolve(null, PrincipalKind.Container, null));
    }
}
