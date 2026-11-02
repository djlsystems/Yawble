namespace Harness.Host.Link;

/// <summary>
/// Tells the link client that the stored link changed - attached, replaced or detached - so it
/// restarts on the new one or stops.
/// </summary>
public interface ILinkSignal
{
    void Changed();
}
