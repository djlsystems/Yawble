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

        var ports = PreviewPorts.Parse("8081, 8082,8081,eighty,0,65536", lines.Add);

        Assert.Equal([8081, 8082], ports);
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("'eighty'", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("'65536'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_port_taken_on_the_machine_is_skipped_with_one_line_and_the_rest_open()
    {
        var lines = new List<string>();

        var open = PreviewPorts.Open([8081, 8082, 8083], port => port != 8082, lines.Add);

        Assert.Equal([8081, 8083], open.Order());
        Assert.Equal("Preview port 8082 is already taken on this machine; it is not forwarded.", Assert.Single(lines));
    }

    [Theory]
    [InlineData(8081, 18081)]
    [InlineData(28081, 18081)]
    [InlineData(18081, 28081)]
    [InlineData(45000, 25000)]
    [InlineData(65535, 25535)]
    [InlineData(80, 10080)]
    public void A_published_port_maps_to_an_internal_port_above_the_privileged_and_below_the_outgoing_range(int published, int expected) =>
        Assert.Equal(expected, PreviewPorts.Map([published], _ => { })[published]);

    [Fact]
    public void The_default_range_maps_one_to_one_onto_internal_ports_none_of_them_published()
    {
        var published = Enumerable.Range(28081, 10).ToList();

        var map = PreviewPorts.Map(published, _ => { });

        Assert.Equal(Enumerable.Range(18081, 10), published.Select(p => map[p]));
    }

    [Fact]
    public void Ports_whose_steps_collide_each_get_their_own_internal_port_and_never_a_published_one()
    {
        var lines = new List<string>();

        var map = PreviewPorts.Map([28081, 8081, 18081], lines.Add);

        // Every step of each lands on another published port or outside the internal range, so each
        // takes the lowest free port from 10000 up, lowest published port first.
        Assert.Equal(10000, map[8081]);
        Assert.Equal(10001, map[18081]);
        Assert.Equal(10002, map[28081]);
        Assert.Empty(lines);
    }

    [Fact]
    public void The_mapping_is_the_same_whatever_order_the_ports_are_given_in() =>
        Assert.Equal(
            PreviewPorts.Map([8081, 28081, 18081, 9000], _ => { }).OrderBy(p => p.Key),
            PreviewPorts.Map([9000, 18081, 28081, 8081], _ => { }).OrderBy(p => p.Key));

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
