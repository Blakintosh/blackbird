using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.Models;

public partial class ModItem : ObservableObject
{
    public string Name { get; }
    public string FolderPath { get; }
    public ObservableCollection<ModZoneFile> ZoneFiles { get; } = [];

    public ModItem(string name, string folderPath)
    {
        Name = name;
        FolderPath = folderPath;
    }
}

public partial class ModZoneFile : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string ZoneName { get; }
    public string ModName { get; }

    public ModZoneFile(string zoneName, string modName)
    {
        ZoneName = zoneName;
        ModName = modName;
    }
}
