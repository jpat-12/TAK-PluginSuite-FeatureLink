using System;
using System.Windows;

namespace FeatureLink.Services
{
    /// <summary>
    /// The plugin's modal-dialog boundary.
    ///
    /// Two reasons this exists rather than <c>MessageBox.Show</c> calls scattered through the view
    /// model: (1) a direct <c>MessageBox.Show</c> from a view model blocks any headless test run
    /// outright, which is part of why the WinTAK tree had zero tests; (2) C-02 needs one auditable
    /// place where a network peer's layer share is accepted or refused.
    ///
    /// The hooks are settable so a test can drive the accept/decline branches deterministically.
    /// In production they are the real WPF dialogs.
    /// </summary>
    public static class DialogService
    {
        /// <summary>Yes/No confirmation. Replaceable for tests.</summary>
        public static Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

        /// <summary>Accept/decline prompt for an inbound layer share. Replaceable for tests.</summary>
        public static Func<ShareImportRequest, bool> ShareImportHandler { get; set; } = DefaultShareImport;

        public static bool Confirm(string message, string caption) => ConfirmHandler(message, caption);

        /// <summary>Describes an inbound ".featurelinkshare" for the consent prompt.</summary>
        public sealed class ShareImportRequest
        {
            public string SourceFileName { get; set; }
            public string LayerName { get; set; }
            public string TargetUrl { get; set; }
            public bool IsPrivate { get; set; }
            public bool HasDisplayConfig { get; set; }
        }

        /// <summary>
        /// C-02: a received share used to be auto-added and auto-downloaded with no prompt at all,
        /// so any peer who could send a Mission Package could inject arbitrary markers into
        /// another operator's common operational picture without interaction — and drive an
        /// outbound HTTP request to a URL of their choosing while doing it.
        ///
        /// The prompt names the source file <b>and</b> the exact URL that will be contacted, since
        /// the URL is the part that actually carries risk and the part a peer controls.
        /// </summary>
        public static bool ConfirmShareImport(string sourceFileName, string layerName, string targetUrl,
            bool isPrivate, bool hasDisplayConfig)
        {
            return ShareImportHandler(new ShareImportRequest
            {
                SourceFileName = sourceFileName,
                LayerName = layerName,
                TargetUrl = targetUrl,
                IsPrivate = isPrivate,
                HasDisplayConfig = hasDisplayConfig,
            });
        }

        private static bool DefaultConfirm(string message, string caption)
        {
            return MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;
        }

        private static bool DefaultShareImport(ShareImportRequest request)
        {
            string message =
                "Another TAK user has sent you a FeatureLink layer.\n\n"
                + "Received file:\n    " + (request.SourceFileName ?? "(unknown)") + "\n\n"
                + "Layer name:\n    " + (request.LayerName ?? "(none supplied)") + "\n\n"
                + "Accepting will contact this URL and add its features to your map:\n    "
                + (request.TargetUrl ?? "(none)") + "\n\n"
                + "Sharing: " + (request.IsPrivate ? "private (requires your ArcGIS sign-in)" : "public")
                + "\nDisplay styling included: " + (request.HasDisplayConfig ? "yes" : "no") + "\n\n"
                + "Only accept this if you recognise the sender and the URL above.\n\n"
                + "Add this layer?";

            // MessageBoxResult.No is the default button so a distracted Enter/space press declines
            // rather than accepts.
            return MessageBox.Show(message, "Accept shared FeatureLink layer?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
    }
}
