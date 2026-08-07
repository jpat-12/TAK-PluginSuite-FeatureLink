using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>Typed failure from an ArcGIS REST call — either a transport failure or an
    /// <c>{"error":{...}}</c> body returned with HTTP 200.</summary>
    public sealed class ArcGisServiceException : Exception
    {
        public int Code { get; }
        public string Details { get; }
        /// <summary>True when ArcGIS reported an authentication/authorization failure (498/499/403)
        /// — the caller should prompt for a fresh sign-in rather than retrying.</summary>
        public bool IsAuthFailure => Code == 498 || Code == 499 || Code == 403 || Code == 401;

        public ArcGisServiceException(string message, int code = 0, string details = null, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
            Details = details;
        }
    }

    /// <summary>
    /// The single ArcGIS response guard for this codebase (C-22).
    ///
    /// ArcGIS returns application errors as <c>{"error":{"code":498,"message":"Invalid token"}}</c>
    /// with <b>HTTP 200</b>. Before this existed, seven parse sites in
    /// <see cref="ArcGisFeatureService"/> read <c>json["count"]</c> / <c>json["features"]</c>
    /// straight off such a body, so a 401, a deleted layer and a genuinely empty layer were all
    /// reported to the operator as "0 features" — a silent-wrong-data failure on a tactical
    /// display. Every parse now goes through <see cref="GetJsonAsync"/> or
    /// <see cref="PostFormJsonAsync"/>, which check the transport status <b>and</b> the error body
    /// and raise a typed <see cref="ArcGisServiceException"/>.
    ///
    /// C-21: the access token travels in the <c>X-Esri-Authorization: Bearer</c> header, never in
    /// the query string or a form field, and every URL is passed through
    /// <see cref="Log.Redact"/> before it can reach a log line.
    /// </summary>
    public static class ArcGisHttp
    {
        /// <summary>Esri's documented header form for a bearer token. (Plain
        /// <c>Authorization: Bearer</c> is also accepted by ArcGIS Online; the Esri-prefixed
        /// header is used because some proxies strip or rewrite <c>Authorization</c>, and Esri
        /// documents this one specifically for its REST endpoints.)</summary>
        public const string EsriAuthorizationHeader = "X-Esri-Authorization";

        public static async Task<JObject> GetJsonAsync(HttpClient http, string url, string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyToken(request, token);
                return await SendForJsonAsync(http, request, url, cancellationToken).ConfigureAwait(false);
            }
        }

        public static async Task<JObject> PostFormJsonAsync(HttpClient http, string url,
            IDictionary<string, string> form, string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new FormUrlEncodedContent(form);
                ApplyToken(request, token);
                return await SendForJsonAsync(http, request, url, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Sends an already-built request (used for the multipart addItem upload) and
        /// applies the same status + error-body guard.</summary>
        public static async Task<JObject> SendForJsonAsync(HttpClient http, HttpRequestMessage request,
            string urlForDiagnostics, CancellationToken cancellationToken = default(CancellationToken))
        {
            string safeUrl = Log.Redact(urlForDiagnostics ?? request.RequestUri?.ToString());
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("ArcGIS request failed (transport): " + safeUrl, ex);
                throw new ArcGisServiceException("Could not reach the ArcGIS service: " + ex.Message, 0, safeUrl, ex);
            }

            using (response)
            {
                long? declared = response.Content?.Headers?.ContentLength;
                if (declared.HasValue && declared.Value > SafeJson.MaxServiceResponseBytes)
                    throw new ArcGisServiceException(
                        $"ArcGIS response is {declared.Value} bytes, over the size limit.", 0, safeUrl);

                string body = response.Content == null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error(string.Format(CultureInfo.InvariantCulture,
                        "ArcGIS request returned HTTP {0} for {1}", (int)response.StatusCode, safeUrl));
                    throw new ArcGisServiceException(
                        string.Format(CultureInfo.InvariantCulture, "ArcGIS returned HTTP {0} ({1}).",
                            (int)response.StatusCode, response.ReasonPhrase),
                        (int)response.StatusCode, Truncate(body, 512));
                }

                JObject json;
                try
                {
                    json = SafeJson.ParseObject(body, SafeJson.MaxServiceResponseBytes);
                }
                catch (Exception ex)
                {
                    Log.Error("ArcGIS response was not JSON: " + safeUrl, ex);
                    throw new ArcGisServiceException(
                        "The ArcGIS service returned a response that was not JSON — the URL may not be a "
                        + "Feature Service, or a sign-in page was returned instead.",
                        0, Truncate(body, 512), ex);
                }

                EnsureNoError(json, safeUrl);
                return json;
            }
        }

        /// <summary>Throws if the body carries an ArcGIS <c>error</c> object. Public so the few
        /// places that must parse a body they already hold can route through the same guard.</summary>
        public static void EnsureNoError(JObject json, string urlForDiagnostics = null)
        {
            var error = json?["error"] as JObject;
            if (error == null) return;

            int code = (int?)error["code"] ?? 0;
            string message = (string)error["message"] ?? "Unknown ArcGIS error";
            string details = null;
            if (error["details"] is JArray detailArr && detailArr.Count > 0)
                details = string.Join("; ", detailArr.ToObject<string[]>());
            details = details ?? (string)error["description"];

            Log.Error(string.Format(CultureInfo.InvariantCulture,
                "ArcGIS error {0}: {1}{2} ({3})", code, message,
                details == null ? string.Empty : " — " + details, Log.Redact(urlForDiagnostics)));

            throw new ArcGisServiceException(
                details == null ? message : message + " — " + details, code, details);
        }

        private static void ApplyToken(HttpRequestMessage request, string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            // C-21: header, never "?token=". Both spellings are sent because ArcGIS Enterprise
            // 10.x honours the Esri-prefixed header while ArcGIS Online accepts either.
            request.Headers.TryAddWithoutValidation(EsriAuthorizationHeader, "Bearer " + token);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        /// <summary>Enables TLS 1.2/1.3 explicitly. On a hardened or downlevel Windows image the
        /// .NET Framework default can still exclude TLS 1.2, which surfaces as an opaque
        /// <c>SecureChannelFailure</c> on every HTTPS call. Numeric casts are used so the source
        /// also compiles against runtimes whose enum lacks <c>Tls13</c>.</summary>
        public static void EnsureTlsConfigured()
        {
            try
            {
                const SecurityProtocolType tls12 = (SecurityProtocolType)3072;
                const SecurityProtocolType tls13 = (SecurityProtocolType)12288;
#pragma warning disable SYSLIB0014
                ServicePointManager.SecurityProtocol |= tls12 | tls13;
#pragma warning restore SYSLIB0014
            }
            catch
            {
                // Platform does not support one of the values (or manages TLS itself) — the OS
                // default applies. Never fail plugin start-up over this.
                try
                {
#pragma warning disable SYSLIB0014
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
#pragma warning restore SYSLIB0014
                }
                catch { }
            }
        }
    }
}
