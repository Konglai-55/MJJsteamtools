using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SteamLuaManager.Controls;

/// <summary>A plain search input with a reusable clear action and consistent visual template.</summary>
public sealed class SearchTextBox : TextBox
{
    public static readonly RoutedCommand ClearCommand = new(nameof(ClearCommand), typeof(SearchTextBox));

    static SearchTextBox()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(SearchTextBox),
            new CommandBinding(ClearCommand, Clear, CanClear));
    }

    private static void CanClear(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is SearchTextBox box && box.IsEnabled && box.Text.Length > 0;
        e.Handled = true;
    }

    private static void Clear(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is SearchTextBox box)
        {
            box.Clear();
            box.Focus();
        }

        e.Handled = true;
    }
}
