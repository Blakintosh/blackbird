using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Blackbird.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.ViewModels;

/// <summary>
/// Shared state for dialogs whose primary button does the work while they stay open:
/// inputs lock, the button shows busy (only past 150 ms), and a failure lands in
/// <see cref="ErrorMessage"/> with the user's input intact.
/// </summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(150);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private bool _showBusy;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>The exception behind <see cref="ErrorMessage"/>, shown as its tooltip.</summary>
    [ObservableProperty] private string? _errorDetail;

    public bool IsIdle => !IsBusy;

    /// <summary>Runs <paramref name="work"/>; false (with ErrorMessage set) if it threw.</summary>
    public async Task<bool> RunBusyAsync(Func<Task> work)
    {
        if (IsBusy)
            return false;

        IsBusy = true;
        ErrorMessage = null;
        ErrorDetail = null;
        using var indicator = DispatcherTimer.RunOnce(() => ShowBusy = IsBusy, BusyIndicatorDelay);
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeFailure(ex);
            ErrorDetail = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            ShowBusy = false;
        }
    }

    /// <summary>The one plain line shown for a failure; the exception's own text goes to <see cref="ErrorDetail"/>.</summary>
    protected virtual string DescribeFailure(Exception ex) => ErrorText.Describe("That didn't finish.", ex);
}
