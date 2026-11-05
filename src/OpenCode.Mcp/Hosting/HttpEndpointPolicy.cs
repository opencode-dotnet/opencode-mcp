using System.Net;

namespace OpenCode.Mcp.Hosting;

/// <summary>Defines the listening-address and browser-origin boundaries for local HTTP.</summary>
public static class HttpEndpointPolicy
{
    private const string InvalidUrl = "HTTP URLs must use HTTP and a loopback host, without user information, a path, query or fragment.";

    /// <summary>Resolves loopback HTTP listening addresses and refuses conflicting endpoint configuration.</summary>
    /// <param name="configuration">The host's effective configuration.</param>
    /// <returns>Ordered numeric loopback HTTP authorities.</returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">An endpoint violates the local HTTP boundary.</exception>
    public static IReadOnlyList<Uri> ResolveUrls(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            throw new InvalidOperationException("Configure HTTP endpoints with 'urls'; 'Kestrel:Endpoints' is not supported.");
        }

        var configuredValue = configuration["urls"];
        if (configuredValue is null)
        {
            return [new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), 0).Uri];
        }

        return Array.AsReadOnly(configuredValue.Split(';').Select(ResolveAddress).ToArray());
    }

    /// <summary>Allows an absent Origin or a local HTTP or HTTPS origin.</summary>
    /// <param name="origin">The complete header value, or null when the header is absent.</param>
    /// <returns>Whether the request may reach the MCP handler.</returns>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (origin is null)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            && address.Host is "localhost" or "127.0.0.1" or "[::1]"
            && address.UserInfo.Length == 0
            && !origin.AsSpan(address.Scheme.Length + 3).ContainsAny('/', '?', '#')
            && !origin.Any(char.IsWhiteSpace);
    }

    private static Uri ResolveAddress(string configuredValue)
    {
        if (!Uri.TryCreate(configuredValue, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttp
            || address.UserInfo.Length != 0
            || address.AbsolutePath != "/"
            || address.Query.Length != 0
            || address.Fragment.Length != 0)
        {
            throw new InvalidOperationException(InvalidUrl);
        }

        if (address.Host == "localhost")
        {
            return new UriBuilder(address) { Host = IPAddress.Loopback.ToString() }.Uri;
        }

        if (!IPAddress.TryParse(address.Host, out var host) || !IPAddress.IsLoopback(host))
        {
            throw new InvalidOperationException(InvalidUrl);
        }

        return new UriBuilder(address) { Host = host.ToString() }.Uri;
    }
}
