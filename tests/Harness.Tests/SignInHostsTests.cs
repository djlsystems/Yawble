using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// WHERE A SIGN-IN BROWSER MAY GO. The entry's hosts always; another host only as a sign-in flow
/// reaches it (an OAuth, OpenID, SAML or WS-Federation step from an entry page, then that provider's
/// server redirects in a chain started there), recorded as a provider; everything else refused. What a save keeps is the
/// entry's hosts and those providers.
/// </summary>
public sealed class SignInHostsTests
{
    private static SignInHosts Hosts() => new(["site.example", "www.site.example"]);

    private static Uri U(string url) => new(url);

    [Fact]
    public void The_entrys_own_hosts_are_allowed_and_any_other_host_is_refused()
    {
        var hosts = Hosts();

        Assert.Equal(SignInHosts.Verdict.Allow, hosts.TopLevel(U("https://site.example/login"), null));
        Assert.Equal(SignInHosts.Verdict.Allow, hosts.TopLevel(U("https://WWW.Site.Example./"), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://elsewhere.example/"), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://evil.site.example.attacker.test/"), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("file:///etc/passwd"), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("chrome://settings"), "site.example"));
        Assert.Empty(hosts.Providers);
    }

    [Fact]
    public void An_oauth_step_from_an_entry_page_admits_its_provider_whose_own_redirects_follow()
    {
        var hosts = Hosts();
        var authorize = U("https://accounts.idp.example/o/oauth2/auth?client_id=abc&redirect_uri=https%3A%2F%2Fsite.example%2Fcb&response_type=code");

        Assert.Equal(SignInHosts.Verdict.Admit, hosts.TopLevel(authorize, "site.example"));
        Assert.Equal(SignInHosts.Verdict.Allow, hosts.TopLevel(U("https://accounts.idp.example/signin/challenge"), "accounts.idp.example"));
        // The provider's server sends the chain the person started on the entry page to its next host.
        Assert.Equal(SignInHosts.Verdict.Admit, hosts.TopLevel(U("https://login.idp-cdn.example/mfa"), "site.example", redirectFrom: "accounts.idp.example"));
        Assert.Equal(SignInHosts.Verdict.Allow, hosts.TopLevel(U("https://site.example/cb?code=x"), "accounts.idp.example"));

        Assert.Equal(["accounts.idp.example", "login.idp-cdn.example"], hosts.Providers);
        Assert.Equal(["site.example", "www.site.example", "accounts.idp.example", "login.idp-cdn.example"], hosts.Kept);
    }

    [Fact]
    public void From_a_providers_page_a_link_or_a_redirect_to_a_host_not_yet_admitted_is_refused()
    {
        var hosts = Hosts();
        hosts.TopLevel(U("https://accounts.idp.example/auth?client_id=a&redirect_uri=https%3A%2F%2Fsite.example%2Fcb"), "site.example");

        // A plain link on the provider's page, and one shaped like a sign-in step.
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("http://far.example/"), "accounts.idp.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://other-idp.example/auth?client_id=a&redirect_uri=b"), "accounts.idp.example"));
        // A chain the person started on the provider's page, redirected by it elsewhere.
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://far.example/"), "accounts.idp.example", redirectFrom: "accounts.idp.example"));
        // The entry's own server redirecting to a host with no sign-in shape.
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://tracker.example/"), "site.example", redirectFrom: "site.example"));
        // A redirect from a host that is neither.
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://far.example/"), "site.example", redirectFrom: "unrelated.example"));

        Assert.Equal(["accounts.idp.example"], hosts.Providers);
        Assert.False(hosts.KeepsCookie("far.example"));
        Assert.False(hosts.KeepsCookie(".other-idp.example"));
    }

    [Fact]
    public void The_entry_servers_redirect_to_a_providers_sign_in_step_admits_it()
    {
        var hosts = Hosts();

        Assert.Equal(
            SignInHosts.Verdict.Admit,
            hosts.TopLevel(U("https://accounts.idp.example/auth?client_id=a&response_type=code"), "site.example", redirectFrom: "site.example"));
        Assert.Equal(["accounts.idp.example"], hosts.Providers);
    }

    [Fact]
    public void A_saml_post_is_a_sign_in_step_and_a_plain_link_out_is_not()
    {
        var hosts = Hosts();

        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://sso.corp.example/help"), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Admit, hosts.TopLevel(U("https://sso.corp.example/saml"), "site.example", "SAMLRequest=abc&RelayState=x"));
        // A provider page cannot be reached from a host that is neither entry nor provider.
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("https://other.example/?client_id=a&redirect_uri=b"), "elsewhere.example"));
    }

    [Fact]
    public void A_private_address_is_never_a_provider_or_a_part_unless_the_entry_is_private()
    {
        var hosts = Hosts();
        var shaped = "?client_id=a&redirect_uri=b";

        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("http://127.0.0.1:8080/" + shaped), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("http://169.254.169.254/latest/meta-data" + shaped), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.TopLevel(U("http://localhost/" + shaped), "site.example"));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.Part(U("http://10.0.0.5/admin")));
        Assert.Equal(SignInHosts.Verdict.Refuse, hosts.Part(U("http://[::1]/")));
        Assert.Equal(SignInHosts.Verdict.Allow, hosts.Part(U("https://cdn.example/app.js")));

        var local = new SignInHosts(["127.0.0.1"]);
        Assert.Equal(SignInHosts.Verdict.Allow, local.Part(U("http://localhost:9000/x")));
    }

    [Fact]
    public void A_save_keeps_cookies_and_storage_of_the_entry_and_the_providers_only()
    {
        var hosts = Hosts();
        hosts.TopLevel(U("https://accounts.idp.example/auth?client_id=a&response_type=code"), "site.example");

        Assert.True(hosts.KeepsCookie(".site.example"));
        Assert.True(hosts.KeepsCookie("www.site.example"));
        Assert.True(hosts.KeepsCookie(".idp.example"));
        Assert.False(hosts.KeepsCookie(".tracker.example"));
        Assert.False(hosts.KeepsCookie("ads.example"));
        Assert.False(hosts.KeepsCookie("evil.example"));

        Assert.True(hosts.KeepsOrigin("https://site.example"));
        Assert.True(hosts.KeepsOrigin("https://accounts.idp.example:443"));
        Assert.False(hosts.KeepsOrigin("https://cdn.example"));
    }
}
