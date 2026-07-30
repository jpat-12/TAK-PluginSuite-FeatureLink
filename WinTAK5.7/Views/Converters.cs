using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FeatureLink.Views
{
    /// <summary>Inverse of the built-in <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>
    /// — WPF ships the forward direction but not this one. Used for "visible only when signed
    /// out" bindings (e.g. the Account overlay's Sign In button).</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value is Visibility v) && v != Visibility.Visible;
    }

    /// <summary>Compares a bound <see cref="int"/> against <c>ConverterParameter</c> (also an
    /// int, supplied as a XAML string). Used to drive the HOME/LAYERS/PLI tab-bar underline
    /// (via <c>Button.Tag</c>) from the single <c>CurrentTabIndex</c> property, mirroring
    /// FeatureLinkDropDownReceiver's <c>tabHome.setSelected(page == 0)</c> pattern.</summary>
    public sealed class IntEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return false;
            if (!int.TryParse(parameter.ToString(), out int target)) return false;
            return System.Convert.ToInt32(value) == target;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
