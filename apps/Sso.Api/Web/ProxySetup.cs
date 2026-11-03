using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Sso.Api.Web;

/// <summary>
/// Behind a reverse proxy (Traefik in the cluster, TLS ended there): the scheme and the client address come from
/// X-Forwarded-Proto / X-Forwarded-For, but only from the networks listed in ForwardedHeaders__KnownNetworks__0, ...
/// (the cluster's pod network). Without the setting - locally, in the tests - nothing changes: the headers are not
/// trusted, so nobody can fake an address. With it, the session cookie is Secure (SameAsRequest sees https), the
/// redirects stay on https and the sign-in limit counts real clients.
/// </summary>
public static class ProxySetup
{
    private const string Section = "ForwardedHeaders:KnownNetworks";

    public static WebApplicationBuilder AddReverseProxy(this WebApplicationBuilder builder)
    {
        var networks = builder.Configuration.GetSection(Section).Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var network in networks)
                o.KnownIPNetworks.Add(IPNetwork.Parse(network));
            // A CDN or load balancer -> reverse proxy -> host: the client is the first address not of a known network.
            o.ForwardLimit = null;
        });
        return builder;
    }

    /// <summary>First in the pipeline: everything after it sees the browser's scheme and address.</summary>
    public static IApplicationBuilder UseReverseProxy(this IApplicationBuilder app) =>
        app.UseForwardedHeaders();
}
