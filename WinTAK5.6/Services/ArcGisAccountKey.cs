using System;

namespace FeatureLink.Services
{
    /// <summary>
    /// The identity of one signed-in ArcGIS account: <c>&lt;normalised portal&gt;|&lt;lowercase username&gt;</c>.
    ///
    /// Two accounts are the same account when the portal and the username are the same, and both
    /// halves need normalising before that comparison means anything:
    ///  • the portal arrives from settings.xml, from a typed field and from a share file, so
    ///    "https://www.arcgis.com/" and " https://www.arcgis.com " must not become three accounts —
    ///    <see cref="ArcGisOAuth.NormalizePortal"/> is the single definition of that;
    ///  • ArcGIS usernames are case-insensitive, so signing in as "J.Pattara" after "j.pattara"
    ///    must replace the stored account rather than add a duplicate that then fights it for the
    ///    active slot.
    ///
    /// Case folding is <b>always</b> <c>ToLowerInvariant</c>. Under tr-TR / az-Latn-AZ,
    /// <c>ToLower()</c> maps 'I' to dotless 'ı', so a Turkish-locale operator would key the same
    /// account differently from everyone else — and differently from the blob already on disk.
    /// </summary>
    internal static class ArcGisAccountKey
    {
        /// <summary>Separator between the portal and the username. A URL cannot contain a bare
        /// '|' and an ArcGIS username cannot either, so the first one is unambiguously the join.</summary>
        private const char Separator = '|';

        public static string Make(string portalUrl, string username)
        {
            string portal = ArcGisOAuth.NormalizePortal(portalUrl);
            string user = (username ?? string.Empty).Trim().ToLowerInvariant();
            return portal + Separator + user;
        }

        /// <summary>Splits a key back into its halves. Returns false — rather than partial
        /// output — for anything that was not produced by <see cref="Make"/>, because the caller
        /// uses this on strings read from the token blob, which may be hand-edited or truncated.</summary>
        public static bool TryParse(string key, out string portalUrl, out string username)
        {
            portalUrl = null;
            username = null;
            if (string.IsNullOrEmpty(key)) return false;

            int split = key.IndexOf(Separator);
            if (split <= 0 || split == key.Length - 1) return false;

            portalUrl = key.Substring(0, split);
            username = key.Substring(split + 1);
            return true;
        }

        /// <summary>Label for an account in the switcher. The portal host is appended only when
        /// the same username exists on more than one portal — showing it always turns every
        /// single-account install (nearly all of them) into "jdoe (www.arcgis.com)" noise.</summary>
        public static string DisplayName(string username, string portalHost, bool ambiguous)
        {
            string user = username ?? string.Empty;
            if (!ambiguous || string.IsNullOrWhiteSpace(portalHost)) return user;
            return user + " (" + portalHost.Trim() + ")";
        }

        /// <summary>Host of a portal URL, for <see cref="DisplayName"/>. Falls back to the raw
        /// string: a portal that will not parse as a URI is still better shown than shown blank.</summary>
        public static string PortalHost(string portalUrl)
        {
            if (string.IsNullOrWhiteSpace(portalUrl)) return string.Empty;
            Uri uri;
            if (Uri.TryCreate(portalUrl.Trim(), UriKind.Absolute, out uri)) return uri.Host;
            return portalUrl.Trim();
        }
    }
}
