using System.Diagnostics;

namespace Yawble.Core.Integrations;

/// <summary>Opens a web page for the person (a sign-in page). Tests use a fake; only the host opens a real browser.</summary>
public interface IBrowser
{
    void Open(Uri url);
}

/// <summary>The person's default browser, through the shell. Only http and https addresses are opened (never a file or another scheme).</summary>
public sealed class ShellBrowser : IBrowser
{
    public void Open(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("only a web address can be opened in the browser");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
        {
            throw new McpException($"could not open your browser ({e.GetType().Name}); check that a default browser is set, then try again (the sign-in page is at {url.Host})");
        }
    }
}
