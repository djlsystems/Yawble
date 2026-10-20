using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

public sealed class AgentCatalogFileTests
{
    [Fact]
    public void Save_replaces_the_file_whole_and_leaves_no_temporary_file()
    {
        var root = Directory.CreateTempSubdirectory("harness-catalog-").FullName;
        try
        {
            AgentCatalogFile.Save(root, [Preset("first")]);
            AgentCatalogFile.Save(root, [Preset("second")]);

            var read = Assert.Single(AgentCatalogFile.LoadCustom(root, TextWriter.Null));
            Assert.Equal("second", read.Name);
            Assert.Equal(["agents.json"], Directory.GetFiles(root).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentDefinition Preset(string name) =>
        new(name, AgentMode.Headless, new AgentLaunch("some-cli", ["-p"]));
}
