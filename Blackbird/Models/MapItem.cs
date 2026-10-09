using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.Models;

public partial class MapItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string Name { get; }
    public string FolderPath { get; }
    public string ZoneFilePath { get; }

    public MapItem(string name, string folderPath, string zoneFilePath)
    {
        Name = name;
        FolderPath = folderPath;
        ZoneFilePath = zoneFilePath;
    }
}
