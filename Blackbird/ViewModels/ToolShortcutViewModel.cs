using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.ViewModels;

public partial class ToolShortcutViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _exePath = "";
    [ObservableProperty] private string _arguments = "";
    [ObservableProperty] private Bitmap? _icon;
}
