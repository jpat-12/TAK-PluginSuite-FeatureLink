using System;
using System.Net;
using System.Text.RegularExpressions;

namespace FeatureLink.Services
{
    /// <summary>
    /// Validation for any URL that did not originate in this plugin's own code — a URL typed by
    /// the operator into "Add Layer"/"Join PLI Layer", or one lifted out of a
    /// ".featurelinkshare" file that arrived from a network peer.
    ///
    /// Audit S3/S4: the share importer took <c>o["url"]</c> straight from a peer-supplied Mission
    /// Package and handed it to <c>HttpClient.GetStringAsync</c> with no scheme or host check at
    /// all, making the plugin a network-pivot primitive on the operator's workstation
    /// (<c>http://169.254.169.254/…</c>, <c>http://10.0.0.1/admin</c>, internal port scanning).
    /// The Add-Layer box had the same gap for locally-typed input.
    /// </summary>
    public static class UrlGuard
    {
        public sealed class Result
        {
            public bool Ok { get; }
            public string Reason { get; }
            public Uri Uri { get; }
            private Result(bool ok, string reason, Uri uri) { Ok = ok; Reason = reason; Uri = uri; }
            public static Result Allow(Uri uri) => new Result(true, null, uri);
            public static Result Deny(string reason) => new Result(false, reason, null);
        }

        private static readonly Regex ServicePath = new Regex(
            @"/(Map|Feature|Image)Server(/\d+)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Maximum accepted URL length. ArcGIS service URLs are well under 300 chars;
        /// this bounds what reaches the network stack, a file name, and the UI.</summary>
        public const int MaxUrlLength = 2048;

        /// <summary>Validates a URL that will be fetched over the network.
        /// Requires absolute <c>https</c>, a resolvable non-private host, and (when
        /// <paramref name="requireServicePath"/> is set) an ArcGIS <c>*Server[/n]</c> path.</summary>
        public static Result ValidateServiceUrl(string url, bool requireServicePath = true)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Result.Deny("The URL is empty.");
            url = url.Trim();
            if (url.Length > MaxUrlLength)
                return Result.Deny($"The URL is longer than {MaxUrlLength} characters.");

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return Result.Deny("That is not a valid absolute URL.");

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return Result.Deny($"Only https:// URLs are accepted (got \"{uri.Scheme}\").");

            if (uri.IsDefaultPort == false && uri.Port != 443 && !IsPlausiblePort(uri.Port))
                return Result.Deny($"Port {uri.Port} is not accepted.");

            if (IsPrivateOrLoopbackHost(uri))
                return Result.Deny(
                    $"\"{uri.Host}\" resolves to a private, loopback or link-local address, which "
                    + "this plugin will not fetch.");

            if (requireServicePath && !ServicePath.IsMatch(uri.AbsolutePath))
                return Result.Deny(
                    "The URL does not look like an ArcGIS service — it should end in "
                    + "/FeatureServer, /FeatureServer/0 or /MapServer.");

            return Result.Allow(uri);
        }

        private static bool IsPlausiblePort(int port) => port == 443 || port == 6443 || port == 7443;

        /// <summary>Denies loopback, RFC1918, CGNAT, link-local (incl. the 169.254.169.254 cloud
        /// metadata address) and IPv6 unique-local/loopback destinations. A hostname that does not
        /// resolve is allowed through — the request will simply fail — but a hostname that
        /// resolves to any private address is denied post-resolution, which is what defeats a
        /// DNS-rebinding-style bypass of a purely textual check.</summary>
        public static bool IsPrivateOrLoopbackHost(Uri uri)
        {
            IPAddress[] addresses;
            IPAddress literal;
            if (IPAddress.TryParse(uri.Host, out literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try { addresses = Dns.GetHostAddresses(uri.Host); }
                catch { return false; }
            }

            foreach (var ip in addresses)
                if (IsPrivate(ip)) return true;
            return false;
        }

        public static bool IsPrivate(IPAddress ip)
        {
            if (ip == null) return true;
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return true;                                  // 10/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;      // 172.16/12
                if (b[0] == 192 && b[1] == 168) return true;                   // 192.168/16
                if (b[0] == 169 && b[1] == 254) return true;                   // link-local + metadata
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;     // CGNAT 100.64/10
                if (b[0] == 0 || b[0] >= 224) return true;                     // this-network, multicast, reserved
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return true;                        // fc00::/7 unique-local
                if (ip.IsIPv4MappedToIPv6) return IsPrivate(ip.MapToIPv4());
            }
            return false;
        }

        /// <summary>Validates an iconset path before it is emitted as a <c>usericon</c> CoT
        /// attribute. A peer controls this string via a received share, and it is broadcast to the
        /// whole TAK network; only <c>uid/group/file</c> shapes are allowed through.</summary>
        public static bool IsSafeIconsetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 512) return false;
            if (path.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            foreach (char c in path)
            {
                if (char.IsControl(c)) return false;
                if (c == '"' || c == '\'' || c == '<' || c == '>' || c == '&') return false;
            }
            return true;
        }

        /// <summary>Caps and strips control characters from a peer-supplied display name before it
        /// reaches the UI, a MessageBox, a file name or a CoT callsign.</summary>
        public static string SanitizeDisplayName(string name, int maxLength = 128)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(Math.Min(name.Length, maxLength));
            foreach (char c in name)
            {
                if (sb.Length >= maxLength) break;
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            return sb.ToString().Trim();
        }
    }
}
