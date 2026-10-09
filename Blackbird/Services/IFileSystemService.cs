using System.Collections.Generic;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public interface IFileSystemService
{
    string GamePath { get; }
    string ToolsPath { get; }

    List<MapItem> ScanMaps();
    List<ModItem> ScanMods();
    List<string> ScanMapTemplates();
    SetupDetectionResult DetectPaths();
    SetupValidationResult ValidateSetup(string? gamePath = null, string? toolsPath = null);
    string DetectGameLanguage();
    /// <summary>Sets both paths on the calling (UI) thread, then saves the settings in the background.</summary>
    Task SetPathsAsync(string gamePath, string toolsPath);
    WorkshopItemData? ReadWorkshopJson(string zoneFolderPath);
    void WriteWorkshopJson(string zoneFolderPath, WorkshopItemData data);
    WorkshopProfilesData ReadWorkshopProfiles(string zoneFolderPath, WorkshopItemData? fallbackData = null);
    void WriteWorkshopProfiles(string zoneFolderPath, WorkshopProfilesData data);
}
