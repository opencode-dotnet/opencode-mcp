using Microsoft.Extensions.Configuration;
using OpenCode.Mcp.Hosting;
using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tests.Hosting;

/// <summary>Checks the listening and browser-origin boundaries without starting a server.</summary>
public sealed class HttpEndpointPolicyTests
{
    private const string InvalidUrl = "HTTP URLs must use HTTP and a loopback host, without user information, a path, query or fragment.";
    private const string DynamicLoopbackUrl = "http://127.0.0.1:0";

    /// <summary>An absent URL selects a dynamic numeric loopback listener.</summary>
    [Test]
    public async Task ResolveUrls_Should_Return_Default_When_Urls_Are_Unset() =>
        await Assert.That(HttpEndpointPolicy.ResolveUrls(Configuration(null)).Select(static address => address.GetLeftPart(UriPartial.Authority)))
            .IsEquivalentTo([new Uri(DynamicLoopbackUrl).GetLeftPart(UriPartial.Authority)], CollectionOrdering.Matching);

    /// <summary>Numeric loopback authorities retain their port and normalize a root slash.</summary>
    [Test]
    [Arguments(DynamicLoopbackUrl, DynamicLoopbackUrl)]
    [Arguments("http://127.4.3.2:891/", "http://127.4.3.2:891")]
    [Arguments("http://[::1]:0", "http://[::1]:0")]
    [Arguments("http://[0:0:0:0:0:0:0:1]:9123/", "http://[::1]:9123")]
    public async Task ResolveUrls_Should_Preserve_Numeric_Loopback_When_Url_Is_Valid(string configuredValue, string expected) =>
        await Assert.That(HttpEndpointPolicy.ResolveUrls(Configuration(configuredValue)).Select(static address => address.GetLeftPart(UriPartial.Authority)))
            .IsEquivalentTo([new Uri(expected).GetLeftPart(UriPartial.Authority)], CollectionOrdering.Matching);

    /// <summary>Localhost becomes numeric IPv4, including a dynamic port.</summary>
    [Test]
    [Arguments("http://localhost:0", DynamicLoopbackUrl)]
    [Arguments("HTTP://LOCALHOST:6789/", "http://127.0.0.1:6789")]
    public async Task ResolveUrls_Should_Normalize_Localhost_When_Url_Uses_Localhost(string configuredValue, string expected) =>
        await Assert.That(HttpEndpointPolicy.ResolveUrls(Configuration(configuredValue)).Select(static address => address.GetLeftPart(UriPartial.Authority)))
            .IsEquivalentTo([new Uri(expected).GetLeftPart(UriPartial.Authority)], CollectionOrdering.Matching);

    /// <summary>Every URL is checked and the listener order is preserved.</summary>
    [Test]
    public async Task ResolveUrls_Should_Preserve_Order_When_Several_Urls_Are_Configured() =>
        await Assert.That(HttpEndpointPolicy.ResolveUrls(Configuration("http://[::1]:8765;http://localhost:0;http://127.0.0.2:3456"))
                .Select(static address => address.GetLeftPart(UriPartial.Authority)))
            .IsEquivalentTo(new[] { new Uri("http://[::1]:8765"), new Uri(DynamicLoopbackUrl), new Uri("http://127.0.0.2:3456") }
                    .Select(static address => address.GetLeftPart(UriPartial.Authority)),
                CollectionOrdering.Matching);

    /// <summary>Wildcards, unspecified addresses, remote hosts and a bad list member are refused.</summary>
    [Test]
    [Arguments("http://0.0.0.0:0")]
    [Arguments("http://[::]:0")]
    [Arguments("http://*:0")]
    [Arguments("http://+:0")]
    [Arguments("http://192.0.2.1:80")]
    [Arguments("http://[2001:db8::1]:80")]
    [Arguments("http://example.com:80")]
    [Arguments("http://localhost.example.com:80")]
    [Arguments("http://127.0.0.1:0;http://0.0.0.0:0")]
    public async Task ResolveUrls_Should_Reject_Urls_When_Host_Is_Not_Loopback(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>Listeners use HTTP only, not TLS or another URI scheme.</summary>
    [Test]
    [Arguments("https://127.0.0.1:443")]
    [Arguments("ftp://localhost:21")]
    public async Task ResolveUrls_Should_Reject_Urls_When_Scheme_Is_Not_Http(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>Endpoint authorities cannot contain credentials.</summary>
    [Test]
    [Arguments("http://user@localhost:80")]
    [Arguments("http://user:pass@127.0.0.1:80")]
    public async Task ResolveUrls_Should_Reject_Urls_When_User_Information_Is_Present(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>A listener cannot encode an application path.</summary>
    [Test]
    [Arguments("http://localhost:80/mcp")]
    [Arguments("http://localhost:80//")]
    public async Task ResolveUrls_Should_Reject_Urls_When_Path_Is_Present(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>Queries and fragments are not listener authorities.</summary>
    [Test]
    [Arguments("http://localhost:80/?x=1")]
    [Arguments("http://localhost:80/#part")]
    public async Task ResolveUrls_Should_Reject_Urls_When_Query_Or_Fragment_Is_Present(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>Malformed and explicitly blank URL entries fail closed.</summary>
    [Test]
    [Arguments("not a url")]
    [Arguments("http://[::1")]
    [Arguments("http://127.0.0.1:99999")]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(";http://localhost:0")]
    [Arguments("http://localhost:0;")]
    [Arguments("http://localhost:0;;http://[::1]:0")]
    public async Task ResolveUrls_Should_Reject_Urls_When_Url_Is_Malformed(string configuredValue) => await RejectUrl(configuredValue);

    /// <summary>A Kestrel value or subtree cannot override even a valid loopback URL.</summary>
    [Test]
    [Arguments("Kestrel:Endpoints", "configured")]
    [Arguments("Kestrel:Endpoints:Local:Url", DynamicLoopbackUrl)]
    [Arguments("Kestrel:Endpoints:Remote:Url", "http://0.0.0.0:0")]
    public async Task ResolveUrls_Should_Reject_Configuration_When_Kestrel_Endpoints_Are_Configured(string key, string value)
    {
        var configuration = Configuration(DynamicLoopbackUrl);
        configuration[key] = value;
        await Assert.That(() => HttpEndpointPolicy.ResolveUrls(configuration)).ThrowsExactly<InvalidOperationException>()
            .WithMessage("Configure HTTP endpoints with 'urls'; 'Kestrel:Endpoints' is not supported.", StringComparison.Ordinal);
    }

    /// <summary>A missing configuration fails at the public boundary.</summary>
    [Test]
    public async Task ResolveUrls_Should_Reject_Null_When_Configuration_Is_Missing()
    {
        // Deliberately violate the public precondition.
        await Assert.That(() => HttpEndpointPolicy.ResolveUrls(null!)).ThrowsExactly<ArgumentNullException>()
            .WithParameterName("configuration");
    }

    /// <summary>Non-browser clients need not send Origin.</summary>
    [Test]
    public async Task IsAllowedOrigin_Should_Accept_The_Request_When_Origin_Is_Missing() =>
        await Assert.That(HttpEndpointPolicy.IsAllowedOrigin(null)).IsTrue();

    /// <summary>Both browser schemes and all exact local hosts are permitted at any valid port.</summary>
    [Test]
    [Arguments("http://localhost")]
    [Arguments("https://localhost:443")]
    [Arguments("http://127.0.0.1:1234")]
    [Arguments("https://127.0.0.1:9876")]
    [Arguments("http://[::1]")]
    [Arguments("https://[::1]:4443")]
    public async Task IsAllowedOrigin_Should_Accept_The_Request_When_Origin_Is_Loopback(string origin) =>
        await Assert.That(HttpEndpointPolicy.IsAllowedOrigin(origin)).IsTrue();

    /// <summary>Opaque, list-valued, remote and non-origin values are refused.</summary>
    [Test]
    [Arguments("null")]
    [Arguments("")]
    [Arguments("*")]
    [Arguments("http://localhost http://127.0.0.1")]
    [Arguments("http://localhost ")]
    [Arguments("http://localhost,http://127.0.0.1")]
    [Arguments("ftp://localhost")]
    [Arguments("http://example.com")]
    [Arguments("http://localhost.example.com")]
    [Arguments("http://notlocalhost")]
    [Arguments("http://127.0.0.2")]
    [Arguments("http://user@localhost")]
    [Arguments("http://localhost/mcp")]
    [Arguments("http://localhost/")]
    [Arguments("http://localhost?x=1")]
    [Arguments("http://localhost#part")]
    [Arguments("http://localhost:99999")]
    public async Task IsAllowedOrigin_Should_Reject_The_Request_When_Origin_Is_Untrusted_Or_Malformed(string origin) =>
        await Assert.That(HttpEndpointPolicy.IsAllowedOrigin(origin)).IsFalse();

    private static IConfigurationRoot Configuration(string? configuredValue) => new ConfigurationBuilder().AddInMemoryCollection(
        configuredValue is null ? [] : new Dictionary<string, string?>(StringComparer.Ordinal) { ["urls"] = configuredValue }).Build();

    private static async Task RejectUrl(string configuredValue) =>
        await Assert.That(() => HttpEndpointPolicy.ResolveUrls(Configuration(configuredValue))).ThrowsExactly<InvalidOperationException>()
            .WithMessage(InvalidUrl, StringComparison.Ordinal);
}
