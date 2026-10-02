using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdonisUI.Controls;

namespace ValorantPorting.Views;

// A small "type a name" window (renaming an animation, naming a saved scene). Returns null when cancelled.
public static class InputDialog
{
    public static string? Ask(string title, string prompt, string initial = "")
    {
        var window = new AdonisWindow
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow is { IsLoaded: true } main ? main : null
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text;
            window.DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(box);
        layout.Children.Add(buttons);
        window.Content = layout;
        window.Loaded += (_, _) =>
        {
            box.SelectAll();
            Keyboard.Focus(box);
        };
        return window.ShowDialog() == true ? result : null;
    }
}
