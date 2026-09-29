using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services;

// What changed in each version: CHANGELOG.md, built into the app (the release workflow puts the same text on GitHub).
public static class ReleaseNotes
{
    // this version's section of the built-in changelog, or null
    public static string? ForThisVersion() => Section(Read(), UpdateService.CurrentVersion.ToString(3));

    private static string Read()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            return stream is null ? "" : new StreamReader(stream).ReadToEnd();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string? Section(string changelog, string version)
    {
        var lines = changelog.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == $"## {version}");
        if (start < 0) return null;
        var body = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ")).ToArray();
        var text = string.Join('\n', body).Trim();
        return text.Length > 0 ? text : null;
    }

    // Markdown notes as plain, readable text: bullets as "•", no ** or `
    private static string Readable(string notes) =>
        Regex.Replace(Regex.Replace(notes.Replace("\r", ""), @"^\s*[-*] ", "•  ", RegexOptions.Multiline), @"\*\*|`", "");

    // A window with the notes; linkUrl adds an "Open on GitHub" button
    public static void Show(string title, string heading, string notes, string? linkUrl = null)
    {
        var window = new AdonisWindow
        {
            Title = title, Width = 620, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow is { IsLoaded: true } main ? main : null
        };
        var close = new Button { Content = "Close", Margin = new Thickness(6, 0, 0, 0), MinWidth = 80, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (linkUrl is not null)
        {
            var link = new Button { Content = "Open on GitHub", MinWidth = 80 };
            link.Click += (_, _) => AppHelper.Launch(linkUrl);
            buttons.Children.Add(link);
        }
        buttons.Children.Add(close);

        var layout = new DockPanel { Margin = new Thickness(14) };
        var top = new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = Readable(notes), TextWrapping = TextWrapping.Wrap, LineHeight = 20 }
        });
        window.Content = layout;
        window.ShowDialog();
    }
}
