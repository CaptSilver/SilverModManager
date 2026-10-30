using System.Web;
using FluentAssertions;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumEndpointsTests
{
    private static readonly Uri Redirect = new("http://127.0.0.1:54321/callback");

    private static ForumEndpoints WithClient(ForumSite site) => ForumEndpoints.For(site) with { ClientId = "client-123" };

    private static System.Collections.Specialized.NameValueCollection QueryOf(Uri uri) => HttpUtility.ParseQueryString(uri.Query);

    [Theory]
    [InlineData(ForumSite.LoversLab)]
    [InlineData(ForumSite.VectorPlexus)]
    public void NoSiteShipsWithAClientId(ForumSite site)
    {
        ForumEndpoints.For(site).ClientId.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ForumSite.LoversLab)]
    [InlineData(ForumSite.VectorPlexus)]
    public void BothSitesStartWithPkceOn(ForumSite site)
    {
        ForumEndpoints.For(site).UsePkce.Should().BeTrue();
    }

    [Fact]
    public void LoversLabUsesTheApiHostAndTheDownloadsScope()
    {
        var endpoints = ForumEndpoints.For(ForumSite.LoversLab);

        endpoints.Authorize.Should().Be(new Uri("https://api.loverslab.com/oauth/authorize/"));
        endpoints.Token.Should().Be(new Uri("https://api.loverslab.com/oauth/token/"));
        endpoints.ApiBase.Should().Be(new Uri("https://api.loverslab.com/"));
        endpoints.Scope.Should().Be("downloads");
    }

    [Fact]
    public void VectorPlexusUsesItsOwnHostAndScope()
    {
        var endpoints = ForumEndpoints.For(ForumSite.VectorPlexus);

        endpoints.Authorize.Host.Should().Be("vectorplexus.com");
        endpoints.Token.Host.Should().Be("vectorplexus.com");
        endpoints.ApiBase.Host.Should().Be("vectorplexus.com");
        endpoints.Scope.Should().Be("profile get_downloads");
    }

    [Theory]
    [InlineData(ForumSite.LoversLab, "ll-staging", "vp-staging", "ll-staging")]
    [InlineData(ForumSite.VectorPlexus, "ll-staging", "vp-staging", "vp-staging")]
    public void ASettingsOverrideWinsOverTheBuiltInId(ForumSite site, string ll, string vp, string expected)
    {
        var settings = new ForumAuthSettings { LoversLabClientId = ll, VectorPlexusClientId = vp };

        ForumEndpoints.For(site, settings).ClientId.Should().Be(expected);
    }

    [Theory]
    [InlineData(ForumSite.LoversLab)]
    [InlineData(ForumSite.VectorPlexus)]
    public void EmptyOverridesLeaveTheBuiltInIdInPlace(ForumSite site)
    {
        ForumEndpoints.For(site, new ForumAuthSettings()).Should().Be(ForumEndpoints.For(site));
    }

    [Fact]
    public void ABlankOverrideIsIgnored()
    {
        var settings = new ForumAuthSettings { LoversLabClientId = "   " };

        ForumEndpoints.For(ForumSite.LoversLab, settings).ClientId.Should().BeEmpty();
    }

    [Fact]
    public void TheAuthorizeUrlCarriesTheCodeFlowParameters()
    {
        var url = WithClient(ForumSite.LoversLab).BuildAuthorizeUri(Redirect, "the-state", "the-challenge");

        url.GetLeftPart(UriPartial.Path).Should().Be("https://api.loverslab.com/oauth/authorize/");
        var query = QueryOf(url);
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("client-123");
        query["redirect_uri"].Should().Be("http://127.0.0.1:54321/callback");
        query["scope"].Should().Be("downloads");
        query["state"].Should().Be("the-state");
    }

    [Fact]
    public void PkceAddsAnS256Challenge()
    {
        var url = WithClient(ForumSite.LoversLab).BuildAuthorizeUri(Redirect, "s", "the-challenge");

        var query = QueryOf(url);
        query["code_challenge"].Should().Be("the-challenge");
        query["code_challenge_method"].Should().Be("S256");
    }

    [Fact]
    public void WithoutPkceTheChallengeIsLeftOutAndStateStays()
    {
        var url = (WithClient(ForumSite.VectorPlexus) with { UsePkce = false }).BuildAuthorizeUri(Redirect, "the-state", "ignored");

        var query = QueryOf(url);
        query["code_challenge"].Should().BeNull();
        query["code_challenge_method"].Should().BeNull();
        query["state"].Should().Be("the-state");
    }

    [Fact]
    public void PkceWithoutAChallengeIsRefused()
    {
        var build = () => WithClient(ForumSite.LoversLab).BuildAuthorizeUri(Redirect, "s", null);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithoutPkceNoChallengeIsNeeded()
    {
        var build = () => (WithClient(ForumSite.LoversLab) with { UsePkce = false }).BuildAuthorizeUri(Redirect, "s", null);

        build.Should().NotThrow();
    }

    [Fact]
    public void ScopesWithSpacesAreEscaped()
    {
        var url = WithClient(ForumSite.VectorPlexus).BuildAuthorizeUri(Redirect, "s", "c");

        url.Query.Should().Contain("scope=profile%20get_downloads");
        QueryOf(url)["scope"].Should().Be("profile get_downloads");
    }

    [Theory]
    [InlineData("https://127.0.0.1:1/callback")]
    [InlineData("http://localhost:1/callback")]
    [InlineData("http://example.com/callback")]
    [InlineData("nxm://oauth/callback")]
    public void OnlyALoopbackRedirectIsAccepted(string redirect)
    {
        var build = () => WithClient(ForumSite.LoversLab).BuildAuthorizeUri(new Uri(redirect), "s", "c");

        build.Should().Throw<ArgumentException>();
    }
}
