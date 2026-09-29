using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using SteamLuaManager.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ScriptDownloadView : UserControl
{
    private CancellationTokenSource? _suggestionCts;

    private sealed class RecommendationImageFallbackState
    {
        public required int AppId { get; init; }
        public required bool IsPortrait { get; init; }
        public required Queue<string> RemainingUrls { get; init; }
    }

    private static readonly DependencyProperty RecommendationImageFallbackStateProperty =
        DependencyProperty.RegisterAttached(
            "RecommendationImageFallbackState",
            typeof(RecommendationImageFallbackState),
            typeof(ScriptDownloadView));

    public ScriptDownloadView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                    ? s.Load() : null;
                var showInSetting = settings is { ShowCopyLogButton: true };
                if (showInSetting && DataContext is ScriptDownloadViewModel vm)
                {
                    vm.LogLines.CollectionChanged += (_, _) =>
                        CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                    CopyLogButton.Visibility = Visibility.Collapsed;
            }
            catch { CopyLogButton.Visibility = Visibility.Collapsed; }
        };
        Unloaded += (_, _) => _suggestionCts?.Cancel();
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && vm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (DataContext is not ScriptDownloadViewModel vm)
            return;

        if (e.ChosenSuggestion is ScriptDownloadViewModel.SearchSuggestion suggestion)
        {
            sender.Text = suggestion.Name;
            vm.GameId = suggestion.AppId.ToString();
        }
        else if (!string.IsNullOrWhiteSpace(e.QueryText))
        {
            vm.GameId = e.QueryText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(vm.GameId))
            vm.SearchCommand.Execute(null);
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            vm.GameId = sender.Text ?? string.Empty;
            _suggestionCts?.Cancel();

            var query = vm.GameId.Trim();
            if (query.Length < 2)
            {
                _suggestionCts = null;
                vm.SearchSuggestions.Clear();
                sender.IsSuggestionListOpen = false;
                return;
            }

            var requestCts = new CancellationTokenSource();
            _suggestionCts = requestCts;
            try
            {
                await Task.Delay(350, requestCts.Token);
                var count = await vm.UpdateSearchSuggestionsAsync(query, requestCts.Token);
                if (ReferenceEquals(_suggestionCts, requestCts) && sender.IsKeyboardFocusWithin)
                    sender.IsSuggestionListOpen = count > 0;
            }
            catch (OperationCanceledException)
            {
                // A newer input superseded this request.
            }
            finally
            {
                if (ReferenceEquals(_suggestionCts, requestCts))
                    _suggestionCts = null;
                requestCts.Dispose();
            }
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm &&
            e.SelectedItem is ScriptDownloadViewModel.SearchSuggestion suggestion)
        {
            sender.Text = suggestion.Name;
            vm.GameId = suggestion.AppId.ToString();
        }
    }

    private void LogScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer innerScroller)
            e.Handled = SmoothScrollBehavior.HandleWheel(innerScroller, e.Delta);
    }

    private void RecommendationScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, RecommendationScrollViewer) || e.VerticalChange <= 0 ||
            DataContext is not ScriptDownloadViewModel { HasStartedSearch: false,
                HasMoreRecommendations: true, IsLoadingRecommendations: false,
                CanAutoLoadRecommendations: true } vm)
            return;

        var remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining <= Math.Max(360, e.ViewportHeight * 0.6))
            vm.LoadMoreRecommendationsCommand.Execute(true);
    }

    private void OpenSteamStoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScriptDownloadViewModel.FoundGame game })
        {
            Process.Start(new ProcessStartInfo($"steam://store/{game.AppId}")
            {
                UseShellExecute = true
            });
        }
    }

    private void PortraitRecommendationImage_ImageFailed(object sender, ExceptionRoutedEventArgs e) =>
        HandleRecommendationImageFailed(sender, e, isPortrait: true);

    private void WideRecommendationImage_ImageFailed(object sender, ExceptionRoutedEventArgs e) =>
        HandleRecommendationImageFailed(sender, e, isPortrait: false);

    private void HandleRecommendationImageFailed(
        object sender,
        ExceptionRoutedEventArgs e,
        bool isPortrait)
    {
        if (sender is not Image { Tag: ScriptDownloadViewModel.FoundGame game } image)
            return;

        e.Handled = true;
        var state = image.GetValue(RecommendationImageFallbackStateProperty) as RecommendationImageFallbackState;
        if (state == null || state.AppId != game.AppId || state.IsPortrait != isPortrait)
        {
            state = new RecommendationImageFallbackState
            {
                AppId = game.AppId,
                IsPortrait = isPortrait,
                RemainingUrls = BuildRecommendationImageFallbacks(
                    game,
                    isPortrait,
                    image.Source?.ToString())
            };
            image.SetValue(RecommendationImageFallbackStateProperty, state);
        }

        while (state.RemainingUrls.Count > 0)
        {
            var nextUrl = state.RemainingUrls.Dequeue();
            if (!Uri.TryCreate(nextUrl, UriKind.Absolute, out var uri))
                continue;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.EndInit();
                image.SetCurrentValue(Image.SourceProperty, bitmap);
                return;
            }
            catch
            {
                // 当前尺寸不可用时继续尝试下一种 Steam 图片规格。
            }
        }

        image.SetCurrentValue(Image.SourceProperty, null);
    }

    private void RecommendationImage_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is Image image)
            image.ClearValue(RecommendationImageFallbackStateProperty);
    }

    private static Queue<string> BuildRecommendationImageFallbacks(
        ScriptDownloadViewModel.FoundGame game,
        bool isPortrait,
        string? failedUrl)
    {
        var assetRoot = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}";
        var candidates = isPortrait
            ? new[]
            {
				$"{assetRoot}/library_600x900_2x.jpg",
                $"{assetRoot}/library_600x900.jpg",
				$"{assetRoot}/library_capsule.jpg",
                $"{assetRoot}/capsule_616x353.jpg",
                game.CoverUrl,
                $"{assetRoot}/header.jpg",
                $"{assetRoot}/library_hero.jpg"
            }
            : new[]
            {
				$"{assetRoot}/header_schinese.jpg",
				game.CoverUrl,
                $"{assetRoot}/header.jpg",
				$"{assetRoot}/capsule_616x353_schinese.jpg",
				$"{assetRoot}/capsule_616x353.jpg",
                $"{assetRoot}/library_hero.jpg",
				$"{assetRoot}/library_600x900_schinese.jpg",
                $"{assetRoot}/library_600x900_2x.jpg",
                $"{assetRoot}/library_600x900.jpg"
            };

        return new Queue<string>(candidates
            .Where(url => !string.IsNullOrWhiteSpace(url) &&
                          !string.Equals(url, failedUrl, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }
}
