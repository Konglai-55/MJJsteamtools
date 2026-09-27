using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class TrainerView : UserControl
{
    private bool _eventsAttached;

    public TrainerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_eventsAttached) return;
        _eventsAttached = true;
        SearchContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        DownloadContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        BindingContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        HotTrainersScrollViewer.PreviewMouseWheel += OnNestedScrollViewerPreviewMouseWheel;
        NewReleasesScrollViewer.PreviewMouseWheel += OnNestedScrollViewerPreviewMouseWheel;
    }

    private static void OnContentPanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is FrameworkElement element)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                element.Opacity = 1;
                return;
            }

            element.Opacity = 0;
            var opacityAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            element.RenderTransform = transform;
            var positionAnimation = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(FrameworkElement.OpacityProperty, opacityAnimation);
            transform.BeginAnimation(TranslateTransform.YProperty, positionAnimation);
        }
    }

    private static void OnNestedScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer innerSv)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                innerSv.ScrollToHorizontalOffset(innerSv.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }

            var parent = FindVisualParent<ScrollViewer>(innerSv);
            if (parent != null)
            {
                parent.ScrollToVerticalOffset(parent.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        while (element != null)
        {
            element = VisualTreeHelper.GetParent(element);
            if (element is T parent)
                return parent;
        }
        return null;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (DataContext is TrainerViewModel vm && vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }

    private void BindingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TrainerViewModel vm)
            vm.SaveBindingsCommand.Execute(null);
    }
}
