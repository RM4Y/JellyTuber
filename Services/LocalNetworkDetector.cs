using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTuber.Services;

/// <summary>
/// Decides whether a playback request can safely be served with a plain
/// redirect straight to the resolved googlevideo/HLS URL, the way the old
/// YouTubeFast plugin always did.
///
/// That URL is signed and IP-locked to whoever resolved it - this server.
/// A client behind the SAME home router as the server shares that router's
/// public IP for anything it reaches over the internet (including
/// googlevideo), so a redirect works for it exactly as well as it did for
/// the server. A client on a genuinely different network (mobile data,
/// another building) presents a different public IP and gets a 403
/// following the redirect - that's the case JellyTuber's proxy exists for.
///
/// So the test is simply: does the requesting client's IP match this
/// server's own outbound public IP (or is it obviously on the same private
/// network segment)? If yes, redirect (fast, no proxy overhead at all). If
/// no, fall back to the proxied/segmented pipeline.
/// </summary>
internal static class LocalNetworkDetector
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private static string? _cachedServerIp;
    private static DateTime _cachedAtUtc;

    public static async Task<bool> IsSameNetworkAsync(IHttpClientFactory httpClientFactory, IPAddress? clientIp, ILogger logger, CancellationToken ct)
    {
        if (clientIp is null)
        {
            return false;
        }

        if (IsPrivateOrLoopback(clientIp))
        {
            return true;
        }

        var serverIp = await GetServerPublicIpAsync(httpClientFactory, logger, ct).ConfigureAwait(false);
        return serverIp is not null && string.Equals(serverIp, clientIp.ToString(), StringComparison.Ordinal);
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                var bytes = ip.GetAddressBytes();

                // fe80::/10 link-local, fc00::/7 unique local.
                return ip.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
            }
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    private static async Task<string?> GetServerPublicIpAsync(IHttpClientFactory httpClientFactory, ILogger logger, CancellationToken ct)
    {
        if (_cachedServerIp is not null && DateTime.UtcNow - _cachedAtUtc < CacheDuration)
        {
            return _cachedServerIp;
        }

        await RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedServerIp is not null && DateTime.UtcNow - _cachedAtUtc < CacheDuration)
            {
                return _cachedServerIp;
            }

            var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var ip = (await http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();

            if (!IPAddress.TryParse(ip, out _))
            {
                logger.LogWarning("Unexpected response resolving this server's own public IP: '{Response}'", ip);
                return null;
            }

            _cachedServerIp = ip;
            _cachedAtUtc = DateTime.UtcNow;
            return ip;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not determine this server's own public IP; treating playback requests as remote");
            return null;
        }
        finally
        {
            RefreshGate.Release();
        }
    }
}
