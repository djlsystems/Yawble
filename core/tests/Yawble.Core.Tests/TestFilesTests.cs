using Xunit;

namespace Yawble.Core.Tests;

public class TestFilesTests
{
    [Fact]
    public void OnWindows_AJunctionLeadsToItsTarget_AndDeletingItLeavesTheTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("junctions are Windows's");
            return;
        }
        var dir = Directory.CreateTempSubdirectory("yawble-junction-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
            var link = Path.Combine(dir, "link");
            if (!TestFiles.TryCreateJunction(link, target))
            {
                Assert.Skip("this file system refused a junction");
                return;
            }

            Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
            File.WriteAllText(Path.Combine(link, "through.txt"), "written through the link");
            Assert.True(File.Exists(Path.Combine(target, "through.txt")));

            Directory.Delete(link);
            Assert.True(File.Exists(Path.Combine(target, "through.txt")));
        }
        finally
        {
            TestFiles.DeleteTree(dir);
        }
    }
}
