using System.ComponentModel.Composition;
using Prism.Mef.Modularity;
using Prism.Modularity;

namespace FeatureLink
{
    /// <summary>
    /// MEF module entry point for the FeatureLink plugin. WinTAK calls
    /// <see cref="Initialize"/> once at startup, mirroring FeatureLinkLifecycle/
    /// FeatureLinkMapComponent's role on the ATAK side (plugin registration, no
    /// per-tick behavior of its own — all real work happens in
    /// <see cref="ViewModels.FeatureLinkDockPane"/>, wired up via MEF/[Export] instead of
    /// ATAK's DropDownMapComponent.onCreate()).
    /// </summary>
    [ModuleExport(typeof(FeatureLinkModule), InitializationMode = InitializationMode.WhenAvailable)]
    public class FeatureLinkModule : IModule
    {
        public void Initialize()
        {
            // Nothing to do at module init time — the dock pane and its ArcGisAuthService /
            // SettingsStore load persisted state lazily on first construction (see
            // FeatureLinkDockPane's constructor), matching MEF's on-demand pane instantiation.
        }
    }
}
