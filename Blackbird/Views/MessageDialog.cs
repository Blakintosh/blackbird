using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Blackbird.Views;

/// <summary>
/// The one message and confirmation dialog. Anything that tells the user something
/// or asks before acting goes through here rather than building its own Window, so
/// every message has the same anatomy, sizes to its text and scrolls past 560 px.
/// </summary>
internal static class MessageDialog
{
    public enum Choice { Cancel, Primary, Secondary }

    /// <summary>Tells the user something. One button, "Close".</summary>
    /// <param name="folderPath">As for <see cref="ConfirmAsync"/>: shown on its own line, with "Open folder".</param>
    public static async Task ShowAsync(Window owner, string title, string message, string? folderPath = null)
    {
        await ShowCoreAsync(owner, title, message, primaryText: null, secondaryText: null,
            destructive: false, folderPath, closeText: "Close");
    }

    /// <summary>
    /// Asks before acting. True only if the user chose <paramref name="confirmText"/>.
    /// </summary>
    /// <param name="confirmText">The verb the button performs ("Move to Recycle Bin"), never "OK".</param>
    /// <param name="destructive">
    /// Danger style, and Enter does not confirm: data loss takes a deliberate click.
    /// </param>
    /// <param name="folderPath">
    /// Shown under the message on its own line, and when it exists adds "Open folder" so
    /// the user can look first. Leave it out of <paramref name="message"/>.
    /// </param>
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText,
        bool destructive = false,
        string? folderPath = null)
    {
        var choice = await ShowCoreAsync(owner, title, message, confirmText, secondaryText: null,
            destructive, folderPath, closeText: "Cancel");
        return choice == Choice.Primary;
    }

    /// <summary>A message with one or two follow-up actions and "Close".</summary>
    public static Task<Choice> ChooseAsync(
        Window owner,
        string title,
        string message,
        string primaryText,
        string? secondaryText = null) =>
        ShowCoreAsync(owner, title, message, primaryText, secondaryText,
            destructive: false, folderPath: null, closeText: "Close");

    private static async Task<Choice> ShowCoreAsync(
        Window owner,
        string title,
        string message,
        string? primaryText,
        string? secondaryText,
        bool destructive,
        string? folderPath,
        string closeText)
    {
        var choice = Choice.Cancel;
        Window? dialog = null;

        var buttons = new StackPanel { Classes = { "dialog-buttons" } };
        var status = new StackPanel { Orientation = Orientation.Horizontal };

        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            var openButton = new Button { Content = "Open folder", Theme = FindTheme("SubtleButton") };
            openButton.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = folderPath, UseShellExecute = true });
                }
                catch (Exception)
                {
                    // A convenience; failing to open the folder mustn't end the confirmation.
                }
            };
            status.Children.Add(openButton);
        }

        if (primaryText is not null)
        {
            var primary = new Button
            {
                Content = primaryText,
                Theme = FindTheme(destructive ? "DangerButton" : "AccentButton"),
                IsDefault = !destructive,
            };
            primary.Click += (_, _) =>
            {
                choice = Choice.Primary;
                dialog?.Close();
            };
            buttons.Children.Add(primary);
        }

        if (secondaryText is not null)
        {
            var secondary = new Button { Content = secondaryText, Theme = FindTheme("SubtleButton") };
            secondary.Click += (_, _) =>
            {
                choice = Choice.Secondary;
                dialog?.Close();
            };
            buttons.Children.Add(secondary);
        }

        // With nothing to confirm, the only button closes, so it takes both keys and
        // the accent (one accent button per dialog).
        var close = new Button
        {
            Content = closeText,
            IsCancel = true,
            IsDefault = primaryText is null,
            Theme = FindTheme(primaryText is null ? "AccentButton" : "SubtleButton"),
        };
        close.Click += (_, _) => dialog?.Close();
        buttons.Children.Add(close);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });

        // The path on its own line, trimmed in the middle so the drive and the folder
        // name both stay visible; the whole path is in the tooltip.
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var path = new TextBlock
            {
                Text = folderPath,
                Classes = { "hint", "mono" },
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.PathSegmentEllipsis,
            };
            ToolTip.SetTip(path, folderPath);
            body.Children.Add(new Border
            {
                Classes = { "path-well" },
                Child = path,
            });
        }

        var shell = new DialogShell
        {
            Title = title,
            Footer = buttons,
            Status = status,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = body,
            },
        };

        dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            CanResize = false,
            Classes = { "dialog" },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = shell,
        };

        // Keyboard focus starts on the safe choice when the action is destructive.
        var initialFocus = destructive || buttons.Children.Count == 1
            ? close
            : buttons.Children[0];
        dialog.Opened += (_, _) => initialFocus.Focus();

        await dialog.ShowDialog(owner);
        return choice;
    }

    /// <summary>A button theme from Theme.axaml, for dialogs built in code.</summary>
    internal static ControlTheme? FindTheme(string key) =>
        Application.Current?.FindResource(key) as ControlTheme;
}
