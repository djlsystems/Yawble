using Harness.Host;

namespace Harness.Tests.Host;

public sealed class PreviewPortsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void Absent_or_empty_is_no_port(string? value) =>
        Assert.Empty(PreviewPorts.Parse(value, _ => { }));

    [Fact]
    public void The_published_ports_are_read_once_each_and_a_value_that_is_no_port_is_skipped_with_a_line()
    {
        var lines = new List<string>();

        var ports = PreviewPorts.Parse("8081, 8082,8081,eighty,0,60000", lines.Add);

        Assert.Equal([8081, 8082], ports);
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("'eighty'", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("'60000'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_port_taken_on_the_machine_is_skipped_with_one_line_and_the_rest_open()
    {
        var lines = new List<string>();

        var open = PreviewPorts.Open([8081, 8082, 8083], port => port != 8082, lines.Add);

        Assert.Equal([8081, 8083], open.Order());
        Assert.Equal("Preview port 8082 is already taken on this machine; it is not forwarded.", Assert.Single(lines));
    }

    [Fact]
    public void The_app_behind_a_published_port_listens_above_it()
    {
        Assert.Equal(18081, PreviewPorts.BehindPort(8081));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://0.0.0.0:8080;http://0.0.0.0:8081;http://0.0.0.0:8082")]
    [InlineData("http://0.0.0.0:8080;", "http://0.0.0.0:8080;http://0.0.0.0:8081;http://0.0.0.0:8082")]
    [InlineData(null, "http://localhost:5000;http://0.0.0.0:8081;http://0.0.0.0:8082")]
    public void The_preview_ports_are_added_to_the_hosts_own_address_which_stays_first(string? urls, string expected) =>
        Assert.Equal(expected, PreviewPorts.Urls(urls, [8082, 8081]));

    [Fact]
    public void With_no_urls_the_hosts_own_ports_setting_is_kept()
    {
        Assert.Equal(
            "http://*:8080;https://*:8443;http://0.0.0.0:8081",
            PreviewPorts.Urls(null, [8081], httpPorts: "8080", httpsPorts: "8443"));
    }
}
