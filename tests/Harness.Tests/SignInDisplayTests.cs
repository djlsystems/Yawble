using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests;

public sealed class SignInDisplayTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("harness-sign-in-display-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task An_xvfb_whose_owner_is_gone_ends_with_it_instead_of_living_on_as_an_orphan()
    {
        Assert.SkipWhen(!File.Exists(SignInDisplay.DefaultXvfb), "This machine has no Xvfb.");

        var authority = Path.Combine(_folder, "Xauthority");
        SignInDisplay.WriteAuthority(authority, new byte[16]);
        using var started = Process.Start(SignInDisplay.StartInfo(SignInDisplay.DefaultXvfb, _folder, 640, 480, authority))!;
        _ = started.StandardError.ReadToEndAsync(CancellationToken.None);
        int? xvfb = null;
        try
        {
            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                bound.CancelAfter(TimeSpan.FromSeconds(15));
                Assert.True(int.TryParse(await started.StandardOutput.ReadLineAsync(bound.Token), out _), "Xvfb did not start a display.");
            }

            xvfb = XvfbUnder(started.Id);
            Assert.NotNull(xvfb);

            // What an owner killed with SIGKILL leaves: its end of the pipe closed, nothing disposed.
            started.StandardInput.Close();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Alive(xvfb.Value) && DateTime.UtcNow < deadline) await Task.Delay(100, Ct);
            Assert.False(Alive(xvfb.Value), "Xvfb lived on after its owner's pipe closed.");
        }
        finally
        {
            if (!started.HasExited) started.Kill(entireProcessTree: true);
        }
    }

    /// <summary>The Xvfb started as <paramref name="id"/>, or as its child.</summary>
    private static int? XvfbUnder(int id)
    {
        if (Name(id) == "Xvfb") return id;
        var children = File.ReadAllText($"/proc/{id}/task/{id}/children").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse);
        return children.FirstOrDefault(c => Name(c) == "Xvfb") is var child and > 0 ? child : null;
    }

    private static string? Name(int id)
    {
        try
        {
            return File.ReadAllText($"/proc/{id}/comm").Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Alive(int id) =>
        Name(id) is not null && !File.ReadAllText($"/proc/{id}/stat").Split(") ")[1].StartsWith('Z');
}
