using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FeatureLink.Tests
{
    /// <summary>Disables the rolling file log for the whole test run — the linked production
    /// <c>Log</c> would otherwise write into the developer's real %AppData%.</summary>
    public static class TestBootstrap
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init() => FeatureLink.Services.Log.FileLoggingEnabled = false;
    }

    /// <summary>Records every request and replays a scripted sequence of responses, so the whole
    /// ArcGIS client can be driven offline. The recorded requests are what the token-placement
    /// test (C-21) asserts against.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses =
            new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public List<string> RequestUris { get; } = new List<string>();

        public StubHandler Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses.Enqueue(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
            return this;
        }

        public StubHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responses.Enqueue(factory);
            return this;
        }

        /// <summary>Repeats the same body for every remaining request.</summary>
        public string AlwaysBody { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestUris.Add(request.RequestUri.ToString());

            if (_responses.Count > 0) return Task.FromResult(_responses.Dequeue()(request));
            if (AlwaysBody != null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(AlwaysBody, System.Text.Encoding.UTF8, "application/json")
                });

            throw new InvalidOperationException(
                "StubHandler ran out of scripted responses; request #" + Requests.Count
                + " was " + request.RequestUri);
        }
    }

    /// <summary>Runs a body under a specific <see cref="System.Globalization.CultureInfo"/> and
    /// restores the previous one. The audit's "classic contract-killer" is de-DE/tr-TR behaviour,
    /// so the culture matrix runs against real cultures rather than being asserted by inspection.</summary>
    public static class CultureScope
    {
        public static void With(string cultureName, Action body)
        {
            var previousCulture = Thread.CurrentThread.CurrentCulture;
            var previousUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                var culture = new System.Globalization.CultureInfo(cultureName);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                body();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previousCulture;
                Thread.CurrentThread.CurrentUICulture = previousUiCulture;
            }
        }
    }

    /// <summary>Locates repository files (the XAML and the view-model source) by walking up from
    /// the test assembly, so the binding smoke test does not depend on a copied artefact.</summary>
    public static class RepoLocator
    {
        public static string Root
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "CONFIG-FORMAT.md"))) return dir.FullName;
                    dir = dir.Parent;
                }
                throw new InvalidOperationException(
                    "Could not locate the repository root from " + AppContext.BaseDirectory);
            }
        }

        public static string Path0(params string[] parts) =>
            System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());
    }
}
