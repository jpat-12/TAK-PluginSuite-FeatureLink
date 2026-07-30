using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;

namespace FeatureLink.Views
{
    /// <summary>
    /// Code-behind for the FeatureLink dock-pane view. All logic lives in
    /// <see cref="ViewModels.FeatureLinkDockPane"/> — this class exists purely so MEF can
    /// construct/export the XAML UserControl, same pattern as ImageSyncView.xaml.cs, plus one
    /// pure view-glue handler below (WPF has no built-in way to open a ContextMenu on a plain
    /// left click — it's normally right-click-only — so the gear button's menu is opened here
    /// rather than in the view model).
    /// </summary>
    [Export]
    public partial class FeatureLinkView : UserControl
    {
        public FeatureLinkView()
        {
            InitializeComponent();
        }

        private void GearButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
            }
        }
    }
}
