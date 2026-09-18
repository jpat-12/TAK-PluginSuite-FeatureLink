using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace FeatureLink.Services
{
    /// <summary>
    /// Turns an exception into something an operator can act on.
    ///
    /// <para><b>Why this exists.</b> The plugin's error <i>detection</i> was already good — the
    /// C-22 guard correctly spots ArcGIS's habit of returning <c>{"error":{"code":499}}</c> with
    /// HTTP 200. What was missing was translation. A field session produced this status line,
    /// eleven times in ninety seconds, while the operator retried the same URL in five different
    /// shapes:</para>
    ///
    /// <code>Could not load layer from URL: Token Required — Token Required</code>
    ///
    /// <para>Every part of that is a failure of communication rather than of logic. It repeats
    /// itself, because ArcGIS puts the same string in <c>message</c> and <c>details</c> and the
    /// formatter concatenated both. It names no cause the operator can recognise — the layer was
    /// not public and they were signed in, so "token" means nothing to them. And it suggests no
    /// action, so the only available move was to guess at the URL.</para>
    ///
    /// <para><b>The rule this file enforces:</b> an operator-facing message says what went wrong
    /// <i>and</i> what to do about it, in the operator's vocabulary — layers, sign-in, network —
    /// never the transport's. Diagnostic precision belongs in <see cref="Log"/>, which keeps the
    /// raw code, the URL and the stack trace. The two audiences are different and want different
    /// text.</para>
    ///
    /// <para>Pure logic by design: no WinTAK types, no I/O. That keeps it inside the SDK-free
    /// test project, so the mapping is asserted rather than hoped for.</para>
    /// </summary>
    public static class ErrorText
    {
        /// <summary>ArcGIS token-related codes. 498 = invalid/expired, 499 = missing.</summary>
        public const int ArcGisInvalidToken = 498;
        public const int ArcGisTokenRequired = 499;

        /// <summary>
        /// The operator-facing sentence for a failure. <paramref name="signedIn"/> changes the
        /// advice materially: "sign in" and "sign in again" are different instructions, and
        /// telling a signed-in operator to sign in is how you get eleven retries.
        /// </summary>
        public static string ForOperator(Exception ex, bool signedIn = false)
        {
            if (ex == null) return "Something went wrong.";

            if (ex is OperationCanceledException || ex is TaskCanceledException)
                return "The operation was cancelled or timed out. Check your connection and try again.";

            if (ex is TimeoutException)
                return "The ArcGIS service did not respond in time. It may be busy — try again shortly.";

            if (ex is ArcGisServiceException arc) return ForArcGis(arc, signedIn);

            string transport = ForTransport(ex);
            if (transport != null) return transport;

            if (ex is UnauthorizedAccessException)
                return "Windows denied access to a file this plugin needs. "
                     + "Check the permissions on %AppData%\\WinTAK\\FeatureLink.";

            if (ex is System.IO.IOException)
                return "A file could not be read or written: " + Clean(ex.Message);

            return Clean(ex.Message);
        }

        private static string ForArcGis(ArcGisServiceException ex, bool signedIn)
        {
            switch (ex.Code)
            {
                case ArcGisTokenRequired:
                case ArcGisInvalidToken:
                case 401:
                    // The single most important message in this file — see the class remarks.
                    return signedIn
                        ? "That layer is not shared publicly, and your ArcGIS sign-in does not open it. "
                        + "Check that the layer is shared with you, or add it from My ArcGIS Layers."
                        : "That layer is not public. Sign in to ArcGIS first, then add it.";

                case 403:
                    return signedIn
                        ? "Your ArcGIS account does not have permission to read that layer. "
                        + "Ask its owner to share it with you."
                        : "That layer is not public. Sign in to ArcGIS first, then add it.";

                case 404:
                    return "No ArcGIS service was found at that URL. Check the address — it should end "
                         + "in /FeatureServer or /FeatureServer/0.";

                case 400:
                    return "ArcGIS rejected the request as invalid: " + Clean(ex.Message)
                         + ". The URL may point at a service this plugin cannot read.";

                case 500:
                case 502:
                case 503:
                case 504:
                    return "The ArcGIS service reported a server error. This is on their side — "
                         + "try again shortly.";
            }

            if (ex.Code >= 500)
                return "The ArcGIS service reported a server error ("
                     + ex.Code.ToString(CultureInfo.InvariantCulture) + "). Try again shortly.";

            return Clean(ex.Message);
        }

        /// <summary>Network-layer failures, which an operator can usually act on (connectivity,
        /// VPN, a typo'd host) and which otherwise surface as raw socket wording.</summary>
        private static string ForTransport(Exception ex)
        {
            // Walk for a SPECIFIC cause first. HttpClient delivers a DNS failure as
            // HttpRequestException wrapping SocketException(HostNotFound), so testing for
            // HttpRequestException inside this loop would match the outer exception on the first
            // iteration and return the generic "check your network" before ever reaching the
            // precise reason. The generic fallback therefore runs only after the whole chain has
            // been searched.
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException sock)
                {
                    switch (sock.SocketErrorCode)
                    {
                        case SocketError.HostNotFound:
                        case SocketError.NoData:
                            return "That server could not be found. Check the address, and whether "
                                 + "this machine has internet access.";
                        case SocketError.TimedOut:
                            return "The connection timed out. Check your network or VPN.";
                        case SocketError.ConnectionRefused:
                            return "The server refused the connection. Check the address and port.";
                        case SocketError.NetworkDown:
                        case SocketError.NetworkUnreachable:
                        case SocketError.HostUnreachable:
                            return "The network is unreachable from this machine.";
                    }
                    return "The connection failed: " + sock.SocketErrorCode + ".";
                }

                if (e is System.Security.Authentication.AuthenticationException)
                    return "The secure connection to the server could not be established. "
                         + "On a hardened Windows image this is usually a TLS configuration problem.";
            }

            // Nothing more specific in the chain — fall back to the generic transport message.
            for (Exception e = ex; e != null; e = e.InnerException)
                if (e is HttpRequestException)
                    return "Could not reach the ArcGIS service. Check your network connection.";

            return null;
        }

        /// <summary>
        /// Tidies a message that is going in front of a person.
        ///
        /// <para>ArcGIS frequently repeats itself — <c>message</c> and <c>details[0]</c> are often
        /// the same string, which the joiner then renders as "Token Required — Token Required".
        /// Repetition reads as a malfunction and costs the reader time, so identical halves are
        /// collapsed. Trailing punctuation is normalised so callers can append a sentence without
        /// producing "..".</para>
        /// </summary>
        public static string Clean(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "Something went wrong.";
            string text = message.Trim();

            foreach (string separator in new[] { " — ", " - ", ": ", "; " })
            {
                int at = text.IndexOf(separator, StringComparison.Ordinal);
                if (at <= 0) continue;
                string left = text.Substring(0, at).Trim();
                string right = text.Substring(at + separator.Length).Trim();
                if (left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                {
                    text = left;
                    break;
                }
            }

            return text.TrimEnd('.', ' ');
        }

        /// <summary>Prefixes an operator message with what was being attempted, without the
        /// stutter of "Add layer failed: Could not..." — the action comes first, the reason
        /// second, and the reason is never allowed to repeat the action.</summary>
        public static string WithContext(string action, Exception ex, bool signedIn = false)
        {
            string reason = ForOperator(ex, signedIn);
            if (string.IsNullOrEmpty(action)) return reason;
            return action.TrimEnd('.', ' ') + " — " + reason;
        }
    }
}
