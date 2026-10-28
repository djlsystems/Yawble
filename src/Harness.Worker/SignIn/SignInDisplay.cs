using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Harness.Host;

/// <summary>
/// ONE SIGN-IN BROWSER'S SCREEN: its own virtual X display (Xvfb), so the browser runs headed, as a
/// person's Chrome does, with nothing on any real screen. The display takes a connection only with
/// the cookie in <see cref="Authority"/> - a file in the browser's own 0700 folder - so nobody else in
/// the container (an agent) can look at the screen or type into it.
/// </summary>
public sealed class SignInDisplay : IAsyncDisposable
{
    /// <summary>Where the image has Xvfb.</summary>
    public const string DefaultXvfb = "/usr/bin/Xvfb";

    private readonly Process _process;

    private SignInDisplay(Process process, string display, string authority, int width, int height)
    {
        _process = process;
        Display = display;
        Authority = authority;
        Width = width;
        Height = height;
    }

    /// <summary>The display's name, such as <c>:3</c>, for <c>DISPLAY</c>.</summary>
    public string Display { get; }

    /// <summary>The cookie file, for <c>XAUTHORITY</c>.</summary>
    public string Authority { get; }

    /// <summary>The screen's size in pixels: what the page reads as <c>screen.width</c> and <c>screen.height</c>.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Starts Xvfb with a <paramref name="width"/> by <paramref name="height"/> screen, no TCP, and a
    /// fresh cookie written into <paramref name="folder"/>; answers once it is taking connections.
    /// </summary>
    public static async Task<SignInDisplay> StartAsync(string folder, int width, int height, string xvfb = DefaultXvfb, CancellationToken ct = default)
    {
        if (!File.Exists(xvfb)) throw new InvalidOperationException("This worker has no Xvfb, so the sign-in browser has no screen to run on.");

        var authority = Path.Combine(folder, "Xauthority");
        WriteAuthority(authority, RandomNumberGenerator.GetBytes(16));

        var start = StartInfo(xvfb, folder, width, height, authority);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Xvfb did not start.");
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await process.StandardOutput.ReadLineAsync(bound.Token);
            if (!int.TryParse(line, out var number)) throw new InvalidOperationException("Xvfb did not start a display.");
            return new SignInDisplay(process, ":" + number, authority, width, height);
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// How Xvfb is started: a <paramref name="width"/> by <paramref name="height"/> screen, no TCP, the
    /// cookie in <paramref name="authority"/>. <c>-displayfd 1</c>: Xvfb picks a free display and writes
    /// its number on stdout once it is ready.
    ///
    /// UNDER A SHELL THAT ENDS IT WITH ITS OWNER: Xvfb does not notice its parent going, and Chrome
    /// does (its pipe closes), so a worker killed while a browser was open left the screen running as
    /// an orphan. The shell reads its stdin - the owner's pipe - until it closes, however the owner
    /// ended, then stops Xvfb. Disposing kills the shell and Xvfb together, as one process tree.
    /// </summary>
    public static ProcessStartInfo StartInfo(string xvfb, string folder, int width, int height, string authority)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("\"$0\" \"$@\" </dev/null & x=$!; cat >/dev/null; kill $x 2>/dev/null; wait $x");
        start.ArgumentList.Add(xvfb);
        foreach (var argument in new[] { "-displayfd", "1", "-screen", "0", $"{width}x{height}x24", "-nolisten", "tcp", "-auth", authority, "-nocursor" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        return start;
    }

    /// <summary>
    /// An Xauthority file with one MIT-MAGIC-COOKIE-1 for any display on any address (FamilyWild):
    /// the display's number is only known once Xvfb has picked it.
    /// </summary>
    public static void WriteAuthority(string path, byte[] cookie)
    {
        using var file = new MemoryStream();
        void Field(byte[] bytes)
        {
            file.WriteByte((byte)(bytes.Length >> 8));
            file.WriteByte((byte)bytes.Length);
            file.Write(bytes);
        }

        file.WriteByte(0xFF);
        file.WriteByte(0xFF);
        Field([]);
        Field([]);
        Field(Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1"));
        Field(cookie);

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(file.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        Kill(_process);
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException)
        {
        }

        _process.Dispose();
        try
        {
            File.Delete(Authority);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
