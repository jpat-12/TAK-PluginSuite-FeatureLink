using System.ComponentModel.Composition;
using System.Windows.Controls;

namespace FeatureLink.Views
{
    /// <summary>
    /// Code-behind for the FeatureLink dock-pane view. All logic lives in
    /// <see cref="ViewModels.FeatureLinkDockPane"/> — this class exists purely so MEF can
    /// construct/export the XAML UserControl, same pattern as ImageSyncView.xaml.cs.
    /// </summary>
    [Export]
    public partial class FeatureLinkView : UserControl
    {
        public FeatureLinkView()
        {
            InitializeComponent();
        }
    }
}
