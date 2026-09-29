using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamLuaManager.Controls;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ExtractionView : UserControl
{
    public ExtractionView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                    ? s.Load() : null;
                var showInSetting = settings is { ShowCopyLogButton: true };
                if (showInSetting && DataContext is ExtractionViewModel vm)
                {
                    vm.LogLines.CollectionChanged += (_, _) =>
                        CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                    CopyLogButton.Visibility = Visibility.Collapsed;
            }
            catch { CopyLogButton.Visibility = Visibility.Collapsed; }

            if (DataContext is ExtractionViewModel scanVm && scanVm.AccountGames.Count == 0 && !scanVm.IsLibraryScanning)
                _ = Dispatcher.BeginInvoke(new Action(() => scanVm.ScanLibraryCommand.Execute(null)),
                    System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExtractionViewModel vm && vm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void LogScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer innerScroller)
            e.Handled = SmoothScrollBehavior.HandleWheel(innerScroller, e.Delta);
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is ExtractionViewModel vm && sender is TextBox box)
            vm.ApplyLibraryFilter(box.Text);
    }

    private void ExtractIndexedGame_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtractionViewModel vm || sender is not Button { Tag: ExtractionGameItem item })
            return;

        AppIdBox.Text = item.AppIdText;
        AppIdBox.Focus();
        AppIdBox.CaretIndex = AppIdBox.Text.Length;
        if (!vm.IsRunning && vm.StartExtractionCommand.CanExecute(null))
            vm.StartExtractionCommand.Execute(null);
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }
}
