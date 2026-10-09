using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Blackbird.Services;

public partial class TemplateService : ITemplateService
{
    private static readonly string[] ValidModZones = ["core_mod", "zm_mod", "mp_mod", "cp_mod"];

    // Only these are safe to round-trip as text for the template-name/GUID
    // substitution; everything else (images, compiled assets, …) must be copied
    // byte-for-byte or it gets corrupted by the UTF-8 decode/encode.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".map", ".zone", ".gsc", ".csc", ".gsh", ".gdt", ".szc",
        ".csv", ".str", ".txt", ".json", ".cfg", ".lua", ".vision", ".atr",
    };

    private readonly IFileSystemService _fileSystem;

    public TemplateService(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    // Path.Combine("", @"usermaps\x") is relative to the working directory, which can be
    // <BO3>\bin: nothing is created until both folders are absolute paths.
    private string GamePath => Rooted(_fileSystem.GamePath, "Black Ops III");
    private string TemplatesRoot => Path.Combine(Rooted(_fileSystem.ToolsPath, "Mod Tools"), "rex", "templates");

    private static string Rooted(string path, string what) =>
        Path.IsPathFullyQualified(path)
            ? path
            : throw new UserMessageException($"The {what} folder isn't set. Choose it in Setup Doctor.");

    public Task<string> CreateFromTemplateAsync(string name, string templateName)
    {
        return Task.Run(() =>
        {
            var sourcePath = Path.Combine(TemplatesRoot, templateName);
            if (!Directory.Exists(sourcePath))
                throw new UserMessageException($"The “{templateName}” map template isn't in {TemplatesRoot}.");

            var gamePath = GamePath;
            var mapFolder = Path.Combine(gamePath, "usermaps", name);
            if (Directory.Exists(mapFolder))
                throw new UserMessageException($"A map folder named {name} already exists.");

            var plan = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Select(file => (Source: (string?)file, Target: Path.Combine(
                    gamePath,
                    ReplaceTemplateToken(Path.GetRelativePath(sourcePath, file), name))))
                .ToList();

            var zoneFile = Path.Combine(mapFolder, "zone_source", $"{name}.zone");
            if (!plan.Any(item => string.Equals(item.Target, zoneFile, StringComparison.OrdinalIgnoreCase)))
                throw new UserMessageException($"The “{templateName}” template has no usermaps\\template\\zone_source\\template.zone.");

            return CopyAll(plan, name);
        });
    }

    public Task<string> CreateModAsync(string name, IEnumerable<string> zoneNames)
    {
        return Task.Run(() =>
        {
            var selectedZones = ValidModZones
                .Where(valid => zoneNames.Contains(valid, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (selectedZones.Length == 0)
                throw new UserMessageException("Choose at least one zone.");

            var modFolder = Path.Combine(GamePath, "mods", name);
            if (Directory.Exists(modFolder))
                throw new UserMessageException($"A mod folder named {name} already exists.");

            var templateZoneSource = Path.Combine(TemplatesRoot, "Mod", "mods", "template", "zone_source");
            var zoneSourceFolder = Path.Combine(modFolder, "zone_source");
            var plan = selectedZones
                .Select(zone => (
                    Source: Path.Combine(templateZoneSource, $"{zone}.zone"),
                    Target: Path.Combine(zoneSourceFolder, $"{zone}.zone")))
                .Select(item => (Source: File.Exists(item.Source) ? item.Source : null, item.Target))
                .ToList();

            return CopyAll(plan, name, extraFolders: [Path.Combine(modFolder, "zone")]);
        });
    }

    /// <summary>
    /// Copies every planned file, refusing up front if any target already exists.
    /// If anything fails part-way, the files and folders this call created are removed again.
    /// </summary>
    private static string CopyAll(List<(string? Source, string Target)> plan, string name, string[]? extraFolders = null)
    {
        var existing = plan.Where(item => File.Exists(item.Target)).Select(item => item.Target).ToList();
        if (existing.Count > 0)
            throw new UserMessageException($"{existing[0]} already exists, and creating the project would overwrite it.");

        var createdFolders = new List<string>();
        var createdFiles = new List<string>();
        var output = new StringBuilder();
        try
        {
            foreach (var (source, target) in plan)
            {
                CreateFolder(Path.GetDirectoryName(target)!, createdFolders);
                CopyFile(source, target, name, createdFiles);
                output.AppendLine(target);
            }

            foreach (var folder in extraFolders ?? [])
                CreateFolder(folder, createdFolders);

            return output.ToString();
        }
        catch
        {
            // Step by step: one file that won't delete mustn't stop the rest of the rollback,
            // or hide the failure the user needs to see.
            foreach (var file in createdFiles)
                TryRollBack(() => File.Delete(file));
            foreach (var folder in createdFolders.OrderByDescending(folder => folder.Length))
            {
                TryRollBack(() =>
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                });
            }

            throw;
        }
    }

    private static void TryRollBack(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the original failure is the one to report.
        }
    }

    private static void CreateFolder(string folder, List<string> createdFolders)
    {
        if (Directory.Exists(folder))
            return;

        CreateFolder(Path.GetDirectoryName(folder)!, createdFolders);
        Directory.CreateDirectory(folder);
        createdFolders.Add(folder);
    }

    /// <summary>
    /// Creates <paramref name="target"/> (never over an existing file) and records it in
    /// <paramref name="createdFiles"/> the moment it exists, so a failed write is still rolled back.
    /// Text files keep the template's encoding and BOM.
    /// </summary>
    private static void CopyFile(string? source, string target, string name, List<string> createdFiles)
    {
        byte[]? bytes = null;
        if (source is null)
        {
            bytes = Encoding.UTF8.GetBytes(BuildFallbackModZone(Path.GetFileNameWithoutExtension(target)));
        }
        else if (TextExtensions.Contains(Path.GetExtension(source)))
        {
            var (content, encoding) = TextFiles.Read(source);
            content = GuidRegex().Replace(content, _ => $"guid \"{Guid.NewGuid().ToString("B").ToUpperInvariant()}\"");
            content = ReplaceTemplateToken(content, name);
            bytes = TextFiles.Encode(content, encoding);
        }

        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        createdFiles.Add(target);
        if (bytes is not null)
        {
            output.Write(bytes);
            return;
        }

        using var input = new FileStream(source!, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.CopyTo(output);
    }

    /// <summary>
    /// The header from the Mod Tools' own mod template, for installs that lack the template.
    /// </summary>
    private static string BuildFallbackModZone(string zone)
    {
        var mode = zone[..zone.IndexOf('_')];
        return $">mode,{mode}\r\n>type,common\r\n\r\n#include \"{zone}.class\"\r\n";
    }

    /// <summary>
    /// Replaces the lowercase word "template" where it stands on its own, so
    /// template_fx.gsc becomes zm_x_fx.gsc but "templates" or "Template" are left alone.
    /// </summary>
    private static string ReplaceTemplateToken(string value, string name) =>
        TemplateTokenRegex().Replace(value, name);

    [GeneratedRegex(@"(?<![A-Za-z0-9_])template(?![A-Za-z0-9])")]
    private static partial Regex TemplateTokenRegex();

    [GeneratedRegex(@"guid ""\{?[0-9a-fA-F\-]+\}?""")]
    private static partial Regex GuidRegex();
}

