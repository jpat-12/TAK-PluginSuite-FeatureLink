using System.ComponentModel.Composition;
using WinTak.Framework.Docking;
using WinTak.Framework.Tools;
using WinTak.Framework.Tools.Attributes;
using FeatureLink.ViewModels;

namespace FeatureLink
{
    /// <summary>
    /// Ribbon toolbar button that opens (or focuses) the FeatureLink dock pane — the WinTAK
    /// equivalent of ATAK's FeatureLinkTool / SHOW_PLUGIN intent
    /// (com.atakmap.android.featurelink.SHOW_PLUGIN), wired declaratively via MEF instead of
    /// AbstractPluginTool + a broadcast action.
    /// </summary>
    [Button(
        ID,
        "FeatureLink",
        LargeImage = "pack://application:,,,/FeatureLink;component/Assets/Large.png",
        SmallImage = "pack://application:,,,/FeatureLink;component/Assets/Small.png",
        ToolTip    = "Sync ArcGIS Feature Layers and send Position Location Information (PLI)",
        Tab        = "Home",
        TabGroup   = "VISTA Tools")]
    [Export(typeof(Button))]
    public class FeatureLinkButton : Button
    {
        internal const string ID = "FeatureLink_FeatureLinkButton";

        private readonly IDockingManager _dockingManager;

        [ImportingConstructor]
        public FeatureLinkButton(IDockingManager dockingManager)
        {
            _dockingManager = dockingManager;
        }

        protected override void OnClick()
        {
            _dockingManager.GetDockPane(FeatureLinkDockPane.ID)?.Activate();
        }
    }
}
