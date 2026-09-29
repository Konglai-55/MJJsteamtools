using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace SteamLuaManager.Controls;

/// <summary>
/// Converts wheel steps into a short, interruptible glide. The target offset is
/// accumulated while the pointer keeps scrolling, so fast wheel input feels like
/// one continuous track instead of a stack of discrete jumps.
/// </summary>
public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Entry point for pages that already own a nested wheel handler.</summary>
    public static bool HandleWheel(ScrollViewer viewer, int delta)
    {
        var horizontal = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
                         viewer.ScrollableWidth > 0;
        var canScroll = horizontal ? viewer.ScrollableWidth > 0 : viewer.ScrollableHeight > 0;
        if (!canScroll)
        {
            var parent = FindParentScrollViewer(viewer);
            return parent is not null && HandleWheel(parent, delta);
        }

        var state = States.GetValue(viewer, v => new ScrollState(v));
        var current = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        var maximum = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
        var baseOffset = state.Timer.IsEnabled && state.Horizontal == horizontal
            ? state.Target
            : current;
        var distance = delta * 0.72;
        state.Horizontal = horizontal;
        state.Target = Math.Clamp(baseOffset - distance, 0, maximum);
        state.Timer.Start();
        return true;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
            viewer.PreviewMouseWheel += Viewer_PreviewMouseWheel;
        else
            viewer.PreviewMouseWheel -= Viewer_PreviewMouseWheel;
    }

    private static void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        var source = e.OriginalSource as DependencyObject;
        var nearest = source is null ? viewer : FindParentScrollViewer(source);
        if (!ReferenceEquals(nearest, viewer)) return;
        e.Handled = HandleWheel(viewer, e.Delta);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
    {
        DependencyObject? current = child is ScrollViewer ? GetParent(child) : child;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer) return scrollViewer;
            current = GetParent(current);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child) =>
        child is Visual or Visual3D
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private sealed class ScrollState
    {
        private readonly ScrollViewer _viewer;
        public readonly DispatcherTimer Timer;
        public double Target;
        public bool Horizontal;

        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            Timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            Timer.Tick += OnTick;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var current = Horizontal ? _viewer.HorizontalOffset : _viewer.VerticalOffset;
            var difference = Target - current;
            if (Math.Abs(difference) < 0.45)
            {
                if (Horizontal) _viewer.ScrollToHorizontalOffset(Target);
                else _viewer.ScrollToVerticalOffset(Target);
                Timer.Stop();
                return;
            }

            // Exponential settling keeps the motion smooth while remaining
            // responsive when a new wheel step arrives mid-glide.
            var next = current + difference * 0.24;
            if (Horizontal) _viewer.ScrollToHorizontalOffset(next);
            else _viewer.ScrollToVerticalOffset(next);
        }
    }
}
