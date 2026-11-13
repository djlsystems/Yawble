using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>A member is named for its job and a first name; its id is that name without what an id cannot hold.</summary>
public class MemberNamesTests
{
    [Theory]
    [InlineData("Developer Priya", "DeveloperPriya")]
    [InlineData("  Tester Wren ", "TesterWren")]
    [InlineData("Data Ingest", "DataIngest")]
    [InlineData("R&D / Tooling", "RDTooling")]
    [InlineData("Ada", "Ada")]
    [InlineData("-_Researcher Amara", "ResearcherAmara")]
    [InlineData("Développeur Zoé", "DveloppeurZo")]
    public void TheIdIsTheNameWithoutWhatAnIdCannotHold(string name, string id) => Assert.Equal(id, MemberNames.IdFor(name));

    [Fact]
    public void ALongNameMakesAnIdOfAtMost32()
    {
        Assert.Equal(32, MemberNames.IdFor("Infrastructure Engineer Bartholomew Montgomery").Length);
        Assert.Throws<ArgumentException>(() => MemberNames.IdFor(new string('a', 61)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("データ")]
    [InlineData("CON")]
    public void ANameWithNoUsableIdIsRefused(string name) => Assert.Throws<ArgumentException>(() => MemberNames.IdFor(name));
}
