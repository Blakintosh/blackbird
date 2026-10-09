using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Blackbird.Services;

namespace Blackbird.Views;

/// <summary>
/// Asks for one line of text (a name, usually) with inline validation. Checks run on
/// lost focus, then live once an error has shown, and the dialog only closes with a
/// valid value.
/// </summary>
internal static class TextPromptDialog
{
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(150);

    /// <param name="validate">Returns why the text can't be used, or null when it can. Gets trimmed text.</param>
    /// <param name="commit">
    /// The work the confirm button does, run while the dialog stays open: the button shows
    /// busy, and a failure stays in the dialog with the text intact. Null closes at once.
    /// </param>
    /// <returns>The trimmed text, or null if the user cancelled.</returns>
    public static async Task<string?> ShowAsync(
        Window owner,
        string title,
        string label,
        string confirmText,
        Func<string, string?> validate,
        string initialText = "",
        string? subtitle = null,
        int maxLength = 80,
        Func<string, Task>? commit = null)
    {
        string? result = null;
        var showErrors = false;
        var busy = false;

        var input = new TextBox { Text = initialText, MaxLength = maxLength };
        var error = new TextBlock { Classes = { "field-error" } };
        var labelBlock = new TextBlock { Text = label, Classes = { "label" } };
        var failure = new TextBlock { Classes = { "error" } };

        var busyLabel = new Panel
        {
            Classes = { "busy-label" },
            Children =
            {
                new TextBlock { Text = confirmText, HorizontalAlignment = HorizontalAlignment.Center },
                new ProgressBar(),
            },
        };
        var confirm = new Button { Content = busyLabel, IsDefault = true, Theme = MessageDialog.FindTheme("AccentButton") };
        AutomationProperties.SetName(confirm, confirmText);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Theme = MessageDialog.FindTheme("SubtleButton") };

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Classes = { "dialog" },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DialogShell
            {
                Title = title,
                Subtitle = subtitle,
                Status = failure,
                Footer = new StackPanel { Classes = { "dialog-buttons" }, Children = { confirm, cancel } },
                Content = new StackPanel
                {
                    Classes = { "field" },
                    Children =
                    {
                        labelBlock,
                        input,
                        new Grid { Classes = { "field-message" }, Children = { error } },
                    },
                },
            },
        };

        bool Check()
        {
            var message = validate((input.Text ?? "").Trim());
            error.Text = showErrors ? message : null;
            return message is null;
        }

        input.LostFocus += (_, _) =>
        {
            showErrors = true;
            Check();
        };
        input.TextChanged += (_, _) =>
        {
            if (showErrors)
                Check();
        };

        confirm.Click += async (_, _) =>
        {
            if (busy)
                return;

            showErrors = true;
            if (!Check())
            {
                input.Focus();
                return;
            }

            var text = (input.Text ?? "").Trim();
            if (commit is { } work && !await RunAsync(work, text))
                return;

            result = text;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        // The work can't be abandoned half-way, so the dialog stays until it ends.
        dialog.Closing += (_, e) =>
        {
            if (busy)
                e.Cancel = true;
        };

        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        await dialog.ShowDialog(owner);
        return result;

        async Task<bool> RunAsync(Func<string, Task> work, string text)
        {
            busy = true;
            failure.Text = null;
            ToolTip.SetTip(failure, null);
            input.IsEnabled = cancel.IsEnabled = false;
            using var indicator = DispatcherTimer.RunOnce(() => busyLabel.Classes.Set("busy", busy), BusyIndicatorDelay);
            try
            {
                await work(text);
                return true;
            }
            catch (Exception ex)
            {
                failure.Text = ErrorText.Describe($"Couldn't {confirmText.ToLowerInvariant()}.", ex);
                ToolTip.SetTip(failure, ex.Message);
                return false;
            }
            finally
            {
                busy = false;
                busyLabel.Classes.Set("busy", false);
                input.IsEnabled = cancel.IsEnabled = true;
                if (failure.Text is not null)
                    input.Focus();
            }
        }
    }
}
