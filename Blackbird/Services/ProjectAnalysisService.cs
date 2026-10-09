using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public partial class ProjectAnalysisService : IProjectAnalysisService
{
    private sealed record GdtAssetReference(string Type, string Name, string FilePath, int LineNumber, int EndLineNumber = 0);
    private sealed record GdtDuplicateRemoval(string FilePath, int StartLine, int EndLine, string Type, string Name, string BlockText);

    private sealed class AnalysisRun(IReadOnlyList<string> projectFiles, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, bool> _fileExists = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> ProjectFiles { get; } = projectFiles;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public bool FileExists(string path)
        {
            if (!_fileExists.TryGetValue(path, out var exists))
            {
                exists = File.Exists(path);
                _fileExists[path] = exists;
            }

            return exists;
        }
    }

    private static readonly string[] ScriptExtensions = [".gsc", ".csc", ".gsh"];
    private static readonly string[] TextReferenceAssetTypes =
    [
        "rawfile",
        "stringtable",
        "luafile",
        "menufile",
        "font",
        "structuredtable",
    ];

    private readonly IFileSystemService _fileSystem;

    public ProjectAnalysisService(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public Task<ProjectAnalysisResult> AnalyzeAsync(ProjectItem project, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => BuildAnalysisResult(project, cancellationToken), cancellationToken);
    }

    public Task<ProjectAnalysisResult> CleanDuplicateGdtAssetsAsync(ProjectItem project)
    {
        return Task.Run(() =>
        {
            var removals = FindDuplicateGdtAssetRemovals(project.FolderPath).ToList();
            if (removals.Count == 0)
            {
                var noWork = BuildAnalysisResult(project);
                noWork.Issues.Insert(0, new ProjectAnalysisIssue
                {
                    Status = "Info",
                    Check = "GDT duplicate cleanup",
                    Detail = "No safely removable duplicate GDT asset blocks were found.",
                    FilePath = project.FolderPath,
                });
                return noWork;
            }

            var outcome = ApplyGdtDuplicateRemovals(removals);
            var result = BuildAnalysisResult(project);

            foreach (var failure in outcome.Failures)
            {
                result.Issues.Insert(0, new ProjectAnalysisIssue
                {
                    Status = "Warning",
                    Check = "GDT duplicate cleanup",
                    Detail = failure.Message,
                    FilePath = failure.FilePath,
                });
            }

            if (outcome.SkippedCount > 0)
            {
                result.Issues.Insert(0, new ProjectAnalysisIssue
                {
                    Status = "Warning",
                    Check = "GDT duplicate cleanup",
                    Detail = $"Skipped {outcome.SkippedCount:N0} block{(outcome.SkippedCount == 1 ? "" : "s")} that changed since the analysis. Analyze again, then clean.",
                    FilePath = project.FolderPath,
                });
            }

            if (outcome.TouchedFiles.Count == 0)
                return result;

            result.Issues.Insert(0, new ProjectAnalysisIssue
            {
                Status = "Info",
                Check = "GDT duplicate cleanup",
                Detail = $"Removed {outcome.RemovedCount:N0} duplicate GDT asset block{(outcome.RemovedCount == 1 ? "" : "s")} from {outcome.TouchedFiles.Count:N0} file{(outcome.TouchedFiles.Count == 1 ? "" : "s")}. The original{(outcome.TouchedFiles.Count == 1 ? " is" : "s are")} in the Recycle Bin.",
                FilePath = outcome.TouchedFiles.FirstOrDefault() ?? project.FolderPath,
            });

            return result;
        });
    }

    private ProjectAnalysisResult BuildAnalysisResult(ProjectItem project, CancellationToken cancellationToken = default)
    {
        var result = new ProjectAnalysisResult
        {
            ProjectName = project.DisplayName,
            ProjectTypeLabel = project.Type == ProjectType.Map ? "Map" : "Mod",
        };

        AnalyzeProject(project, result, cancellationToken);
        if (result.Issues.Count == 0)
        {
            result.Issues.Add(new ProjectAnalysisIssue
            {
                Status = "OK",
                Check = "Project analysis",
                Detail = "Zone files, script references, GDT assets, and common project files look consistent.",
            });
        }

        return result;
    }

    private void AnalyzeProject(ProjectItem project, ProjectAnalysisResult result, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(project.FolderPath))
        {
            Add(result, "Blocker", "Project folder", $"Folder doesn't exist: {project.FolderPath}", project.FolderPath);
            return;
        }

        var files = ListProjectFiles(project.FolderPath, out var listFailure);
        if (listFailure is not null)
        {
            Add(result, "Warning", "Project files",
                ErrorText.Describe("Some of the project's files couldn't be listed, so they weren't checked.", listFailure),
                project.FolderPath);
        }

        var run = new AnalysisRun(files, cancellationToken);
        AnalyzeZoneFiles(project, run, result);
        AnalyzeScriptUsings(project, run, result);
        AnalyzeGdtDuplicates(run, result);
        AnalyzeProjectShape(project, run, result);
    }

    private void AnalyzeZoneFiles(ProjectItem project, AnalysisRun run, ProjectAnalysisResult result)
    {
        var zoneFiles = GetProjectZoneFiles(project).ToList();
        if (zoneFiles.Count == 0)
        {
            Add(result, "Blocker", "Zone files", "No zone files were found for this project.", project.FolderPath);
            return;
        }

        foreach (var zoneFile in zoneFiles)
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            AnalyzeZoneFile(project, run, zoneFile, result);
        }
    }

    private IEnumerable<string> GetProjectZoneFiles(ProjectItem project)
    {
        if (project.Type == ProjectType.Map)
        {
            if (!string.IsNullOrWhiteSpace(project.ZoneFilePath))
                yield return project.ZoneFilePath;
            yield break;
        }

        foreach (var zone in project.ZoneFiles)
            yield return Path.Combine(project.FolderPath, "zone_source", $"{zone.ZoneName}.zone");
    }

    private void AnalyzeZoneFile(ProjectItem project, AnalysisRun run, string zoneFile, ProjectAnalysisResult result)
    {
        if (!run.FileExists(zoneFile))
        {
            Add(result, "Blocker", "Zone file", $"Missing zone file: {zoneFile}", zoneFile);
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(zoneFile);
        }
        catch (Exception ex)
        {
            Add(result, "Blocker", "Zone file", ErrorText.Describe("Couldn't read the zone file.", ex), zoneFile);
            return;
        }

        if (lines.All(line => string.IsNullOrWhiteSpace(RemoveLineComment(line))))
            Add(result, "Warning", "Zone file", "Zone file is empty.", zoneFile);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var cleanLine = RemoveLineComment(lines[i]).Trim();
            if (cleanLine.Length == 0)
                continue;

            if (TryParseZoneInclude(cleanLine, out var includeValue))
            {
                if (!ReferenceExists(run, ZoneIncludeCandidates(project, includeValue)))
                {
                    Add(result, "Warning", "Zone include",
                        $"Include target wasn't found: {includeValue}",
                        zoneFile, lineNumber);
                }

                continue;
            }

            var fields = ParseCsvFields(cleanLine, maxFields: 2);
            if (fields.Count < 2)
                continue;

            var assetType = fields[0].Trim().ToLowerInvariant();
            var assetValue = fields[1].Trim();
            if (assetValue.Length == 0)
                continue;

            if (assetType == "scriptparsetree")
            {
                if (!ReferenceExists(run, ScriptAssetCandidates(project, assetValue)))
                {
                    Add(result, "Blocker", "Script in zone",
                        $"Script referenced by zone wasn't found: {assetValue}",
                        zoneFile, lineNumber);
                }
            }
            else if (TextReferenceAssetTypes.Contains(assetType)
                     && LooksLikeFileReference(assetValue)
                     && !ReferenceExists(run, GenericFileCandidates(project, assetValue)))
            {
                Add(result, "Warning", "File in zone",
                    $"{assetType} reference wasn't found: {assetValue}",
                    zoneFile, lineNumber);
            }
        }
    }

    private void AnalyzeScriptUsings(ProjectItem project, AnalysisRun run, ProjectAnalysisResult result)
    {
        var scripts = FilterByExtension(run.ProjectFiles, ScriptExtensions).ToList();
        if (scripts.Count == 0)
            return;

        foreach (var script in scripts)
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(script);
            }
            catch (Exception ex)
            {
                Add(result, "Warning", "Script scan", ErrorText.Describe("Couldn't read the script.", ex), script);
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var match = ScriptUsingRegex().Match(lines[i]);
                if (!match.Success)
                    continue;

                var usingPath = CleanReferenceValue(match.Groups["path"].Value);
                if (usingPath.Length == 0)
                    continue;

                if (!ReferenceExists(run, ScriptUsingCandidates(project, script, usingPath)))
                {
                    Add(result, "Warning", "#using path",
                        $"Script include target wasn't found: {usingPath}",
                        script, i + 1);
                }
            }
        }
    }

    private void AnalyzeProjectShape(ProjectItem project, AnalysisRun run, ProjectAnalysisResult result)
    {
        if (project.Type == ProjectType.Map)
        {
            var mapFile = Path.Combine(_fileSystem.GamePath, "map_source", ProjectPrefix(project.Name), $"{project.Name}.map");
            if (!File.Exists(mapFile))
            {
                Add(result, "Warning", "Map source",
                    $"Map source wasn't found: {mapFile}",
                    mapFile);
            }
        }

        if (FilterByExtension(run.ProjectFiles, [".xpak"]).Any())
        {
            Add(result, "Info", "Generated XPaks",
                "Generated .xpak files are present. Clean XPaks before publishing if you need a fresh upload.",
                project.FolderPath);
        }
    }

    private static void AnalyzeGdtDuplicates(AnalysisRun run, ProjectAnalysisResult result)
    {
        var unreadable = new List<(string FilePath, string Message)>();
        var assets = EnumerateGdtAssets(FilterByExtension(run.ProjectFiles, [".gdt"]), unreadable, run.CancellationToken).ToList();
        foreach (var (filePath, message) in unreadable)
        {
            Add(result, "Warning", "Unreadable GDT",
                $"Couldn't read {Path.GetFileName(filePath)}, so its assets weren't checked. {message}",
                filePath);
        }

        if (assets.Count == 0)
            return;

        foreach (var duplicate in assets
                     .GroupBy(asset => $"{asset.Type}\0{asset.Name}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.First().Type, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.First().Name, StringComparer.OrdinalIgnoreCase))
        {
            var locations = duplicate
                .Select(asset => $"{Path.GetFileName(asset.FilePath)}:{asset.LineNumber}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var first = duplicate.First();

            Add(result, "Warning", "Duplicate GDT asset",
                $"{first.Type} “{first.Name}” appears {duplicate.Count()} times: {string.Join(", ", locations)}",
                first.FilePath,
                first.LineNumber);
        }
    }

    private static IEnumerable<GdtAssetReference> EnumerateGdtAssets(
        IEnumerable<string> gdtFiles,
        List<(string FilePath, string Message)> unreadable,
        CancellationToken cancellationToken)
    {
        foreach (var gdtFile in gdtFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = File.ReadAllText(gdtFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add((gdtFile, ErrorText.Describe(ex)));
                continue;
            }

            // Almost every GDT is the legacy text format: only something that looks like
            // JSON with an asset list is parsed as JSON, so the rest never throws and catches.
            if (LooksLikeJsonGdt(text))
            {
                var parsedAny = false;
                foreach (var asset in TryParseJsonGdtAssets(gdtFile, text))
                {
                    parsedAny = true;
                    yield return asset;
                }

                if (parsedAny)
                    continue;
            }

            foreach (var asset in TryParseLegacyGdtAssets(gdtFile, text))
                yield return asset;
        }
    }

    private static bool LooksLikeJsonGdt(string text) =>
        text.AsSpan().TrimStart().StartsWith("{")
        && text.Contains("\"assets\"", StringComparison.Ordinal);

    /// <summary>
    /// Later copies of a legacy GDT asset block that are identical to the first one. A duplicate
    /// whose settings differ is still reported by the analysis, but never removed automatically:
    /// which copy the linker should keep is the modder's call.
    /// </summary>
    private static IEnumerable<GdtDuplicateRemoval> FindDuplicateGdtAssetRemovals(string folderPath)
    {
        var gdtFiles = FilterByExtension(ListProjectFiles(folderPath, out _), [".gdt"]);
        var legacyAssets = EnumerateLegacyGdtAssetBlocks(gdtFiles).ToList();
        foreach (var group in legacyAssets
                     .Where(block => block.Asset.EndLineNumber >= block.Asset.LineNumber)
                     .GroupBy(block => $"{block.Asset.Type}\0{block.Asset.Name}", StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderBy(block => block.Asset.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(block => block.Asset.LineNumber)
                .ToList();
            var kept = ordered[0];
            foreach (var (duplicate, blockText) in ordered.Skip(1))
            {
                if (!string.Equals(blockText, kept.BlockText, StringComparison.Ordinal))
                    continue;

                yield return new GdtDuplicateRemoval(
                    duplicate.FilePath,
                    duplicate.LineNumber,
                    duplicate.EndLineNumber,
                    duplicate.Type,
                    duplicate.Name,
                    blockText);
            }
        }
    }

    private sealed record GdtCleanupOutcome(List<string> TouchedFiles, List<(string FilePath, string Message)> Failures, int RemovedCount, int SkippedCount);

    private static GdtCleanupOutcome ApplyGdtDuplicateRemovals(IReadOnlyList<GdtDuplicateRemoval> removals)
    {
        var outcome = new GdtCleanupOutcome([], [], 0, 0);
        var removed = 0;
        var skipped = 0;

        foreach (var fileGroup in removals.GroupBy(removal => removal.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var filePath = fileGroup.Key;
            try
            {
                var (text, encoding) = TextFiles.Read(filePath);
                var lines = SplitLinesKeepingEndings(text);
                var ordered = fileGroup
                    .OrderByDescending(removal => removal.StartLine)
                    .ToList();

                var fileChanged = false;
                foreach (var removal in ordered)
                {
                    var startIndex = removal.StartLine - 1;
                    var count = removal.EndLine - removal.StartLine + 1;
                    if (startIndex < 0 || count <= 0 || startIndex + count > lines.Count
                        || !RemovalStillMatches(lines, startIndex, count, removal))
                    {
                        // The file changed since analysis (or the range is off) —
                        // never delete lines we can't positively re-identify.
                        skipped++;
                        continue;
                    }

                    if (startIndex + count < lines.Count && string.IsNullOrWhiteSpace(lines[startIndex + count]))
                        count++;

                    lines.RemoveRange(startIndex, count);
                    removed++;
                    fileChanged = true;
                }

                if (!fileChanged)
                    continue;

                ReplaceViaRecycleBin(filePath, string.Concat(lines), encoding);
                outcome.TouchedFiles.Add(filePath);
            }
            catch (GdtReplaceException ex)
            {
                outcome.Failures.Add((filePath, ex.Message));
            }
            catch (Exception ex)
            {
                outcome.Failures.Add((filePath, ErrorText.Describe("Couldn't clean this file, so it was left as it was.", ex)));
            }
        }

        return outcome with { RemovedCount = removed, SkippedCount = skipped };
    }

    private static bool RemovalStillMatches(List<string> lines, int startIndex, int count, GdtDuplicateRemoval removal)
    {
        var match = LegacyGdtAssetRegex().Match(RemoveLineComment(lines[startIndex]).Trim());
        return match.Success
            && string.Equals(match.Groups["name"].Value.Trim(), removal.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(match.Groups["type"].Value.Trim(), removal.Type, StringComparison.OrdinalIgnoreCase)
            && string.Equals(BlockTextOf(lines, startIndex, count), removal.BlockText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits text into lines that keep their own line ending, so removing whole lines and joining
    /// the rest gives back every kept byte as it was (mixed CRLF/LF files included).
    /// </summary>
    private static List<string> SplitLinesKeepingEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            lines.Add(text[start..end]);
            start = end;
        }

        return lines;
    }

    /// <summary>A block's lines without indentation or line endings, for telling identical copies apart.</summary>
    private static string BlockTextOf(IReadOnlyList<string> lines, int startIndex, int count) =>
        string.Join("\n", Enumerable.Range(startIndex, count).Select(i => lines[i].Trim()));

    private sealed class GdtReplaceException(string message) : Exception(message);

    /// <summary>
    /// Writes the cleaned GDT beside the original and flushes it to disk, swaps it into place
    /// in one step (File.Replace, so the GDT is never missing or half-written), then sends the
    /// original to the Recycle Bin so the cleanup can be undone from there. Recoverable, so
    /// the cleanup asks no confirmation.
    /// </summary>
    private static void ReplaceViaRecycleBin(string filePath, string text, Encoding encoding)
    {
        var tempPath = filePath + ".blackbird-tmp";
        var backupPath = $"{filePath}.{Guid.NewGuid():N}.blackbird-original";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(TextFiles.Encode(text, encoding));
                stream.Flush(flushToDisk: true);
            }

            File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        if (!RecycleBin.SendBlocking([backupPath]) && File.Exists(backupPath))
        {
            throw new GdtReplaceException(
                $"Cleaned, but the original couldn't go to the Recycle Bin, so it's beside it as {Path.GetFileName(backupPath)}.");
        }
    }

    private static IEnumerable<(GdtAssetReference Asset, string BlockText)> EnumerateLegacyGdtAssetBlocks(IEnumerable<string> gdtFiles)
    {
        foreach (var gdtFile in gdtFiles)
        {
            List<string> lines;
            try
            {
                // Read exactly as the cleanup reads it, so the block texts compare like for like.
                lines = SplitLinesKeepingEndings(TextFiles.Read(gdtFile).Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // the analysis reports unreadable GDTs; there's nothing to clean in one
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var cleanLine = RemoveLineComment(lines[i]).Trim();
                if (cleanLine.Length == 0)
                    continue;

                var match = LegacyGdtAssetRegex().Match(cleanLine);
                if (!match.Success)
                    continue;

                var startLine = i + 1;
                // From the header itself: `"name" ( "type.gdf" ) {` opens the block on the same line.
                var endLine = FindLegacyGdtAssetEndLine(lines, i);
                if (endLine <= 0)
                    continue;

                var name = match.Groups["name"].Value.Trim();
                var type = match.Groups["type"].Value.Trim();
                if (name.Length == 0 || type.Length == 0)
                    continue;

                yield return (new GdtAssetReference(type, name, gdtFile, startLine, endLine),
                    BlockTextOf(lines, i, endLine - i));
            }
        }
    }

    private static int FindLegacyGdtAssetEndLine(IReadOnlyList<string> lines, int searchStartIndex)
    {
        var depth = 0;
        var sawOpen = false;

        for (var i = searchStartIndex; i < lines.Count; i++)
        {
            var line = RemoveLineComment(lines[i]);
            var inQuotes = false;
            foreach (var ch in line)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (ch == '{' && !inQuotes)
                {
                    depth++;
                    sawOpen = true;
                }
                else if (ch == '}' && !inQuotes && sawOpen)
                {
                    depth--;
                    if (depth == 0)
                        return i + 1;
                }
            }
        }

        return 0;
    }

    private static IEnumerable<GdtAssetReference> TryParseJsonGdtAssets(string filePath, string text)
    {
        using var document = TryParseJson(text);
        if (document is null)
            yield break;

        if (!document.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetJsonString(asset, "name");
            var type = GetJsonString(asset, "type")
                       ?? GetJsonString(asset, "_type")
                       ?? GetJsonString(asset, "assetType");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                continue;

            yield return new GdtAssetReference(
                type.Trim(),
                name.Trim(),
                filePath,
                FindJsonNameLine(text, name));
        }
    }

    private static JsonDocument? TryParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch
        {
            return null;
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int FindJsonNameLine(string text, string assetName)
    {
        using var reader = new StringReader(text);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Contains("\"name\"", StringComparison.OrdinalIgnoreCase)
                && line.Contains($"\"{assetName}\"", StringComparison.Ordinal))
            {
                return lineNumber;
            }
        }

        return 1;
    }

    private static IEnumerable<GdtAssetReference> TryParseLegacyGdtAssets(string filePath, string text)
    {
        using var reader = new StringReader(text);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var cleanLine = RemoveLineComment(line).Trim();
            if (cleanLine.Length == 0)
                continue;

            var match = LegacyGdtAssetRegex().Match(cleanLine);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value.Trim();
            var type = match.Groups["type"].Value.Trim();
            if (name.Length == 0 || type.Length == 0)
                continue;

            yield return new GdtAssetReference(type, name, filePath, lineNumber);
        }
    }

    private IEnumerable<string> ZoneIncludeCandidates(ProjectItem project, string reference)
    {
        foreach (var candidate in CandidatePaths(reference, [".zone"], ProjectSearchRoots(project, includeToolsRaw: true)))
            yield return candidate;
    }

    private IEnumerable<string> ScriptAssetCandidates(ProjectItem project, string reference)
    {
        foreach (var candidate in CandidatePaths(reference, ScriptExtensions, ProjectSearchRoots(project, includeToolsRaw: true)))
            yield return candidate;
    }

    private IEnumerable<string> ScriptUsingCandidates(ProjectItem project, string sourceScript, string reference)
    {
        var sourceFolder = Path.GetDirectoryName(sourceScript) ?? project.FolderPath;
        var roots = new List<string> { sourceFolder };
        roots.AddRange(ProjectSearchRoots(project, includeToolsRaw: true));

        foreach (var candidate in CandidatePaths(reference, ScriptExtensions, roots))
            yield return candidate;
    }

    private IEnumerable<string> GenericFileCandidates(ProjectItem project, string reference)
    {
        foreach (var candidate in CandidatePaths(reference, [], ProjectSearchRoots(project, includeToolsRaw: true)))
            yield return candidate;
    }

    private IEnumerable<string> ProjectSearchRoots(ProjectItem project, bool includeToolsRaw)
    {
        yield return project.FolderPath;
        yield return Path.Combine(project.FolderPath, "zone_source");
        yield return _fileSystem.GamePath;
        yield return Path.Combine(_fileSystem.GamePath, "zone_source");

        if (!includeToolsRaw)
            yield break;

        yield return _fileSystem.ToolsPath;
        yield return Path.Combine(_fileSystem.ToolsPath, "raw");
        yield return Path.Combine(_fileSystem.ToolsPath, "share", "raw");
        yield return Path.Combine(_fileSystem.ToolsPath, "share", "raw", "zone_source");
    }

    private static IEnumerable<string> CandidatePaths(string reference, string[] defaultExtensions, IEnumerable<string> roots)
    {
        var clean = CleanReferenceValue(reference);
        if (clean.Length == 0)
            yield break;

        var normalized = clean.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var hasExtension = Path.HasExtension(normalized);
        var referenceVariants = hasExtension
            ? [normalized]
            : defaultExtensions.Length == 0
                ? [normalized]
                : defaultExtensions.Select(extension => normalized + extension).Prepend(normalized).ToArray();

        foreach (var variant in referenceVariants)
        {
            if (Path.IsPathRooted(variant))
                yield return variant;

            foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                yield return Path.Combine(root, variant);

                if (!variant.StartsWith("zone_source" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    yield return Path.Combine(root, "zone_source", variant);
            }
        }
    }

    private static bool ReferenceExists(AnalysisRun run, IEnumerable<string> candidates) =>
        candidates.Any(run.FileExists);

    private static readonly EnumerationOptions AllFilesSkippingInaccessible = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Every file under the project, skipping folders Windows won't open. <paramref name="failure"/>
    /// is set when the walk stopped early, so the analysis says so instead of reporting a clean project.
    /// </summary>
    private static IReadOnlyList<string> ListProjectFiles(string folderPath, out Exception? failure)
    {
        var files = new List<string>();
        failure = null;
        try
        {
            files.AddRange(Directory.EnumerateFiles(folderPath, "*", AllFilesSkippingInaccessible));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        return files;
    }

    private static IEnumerable<string> FilterByExtension(IEnumerable<string> files, string[] extensions)
    {
        var extensionSet = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Where(file => extensionSet.Contains(Path.GetExtension(file)));
    }

    private static bool TryParseZoneInclude(string line, out string includeValue)
    {
        includeValue = "";

        var hashMatch = ZoneHashIncludeRegex().Match(line);
        if (hashMatch.Success)
        {
            includeValue = CleanReferenceValue(hashMatch.Groups["path"].Value);
            return includeValue.Length > 0;
        }

        var fields = ParseCsvFields(line, maxFields: 2);
        if (fields.Count >= 2
            && fields[0].Trim().Equals("include", StringComparison.OrdinalIgnoreCase))
        {
            includeValue = CleanReferenceValue(fields[1]);
            return includeValue.Length > 0;
        }

        return false;
    }

    private static List<string> ParseCsvFields(string line, int maxFields)
    {
        var fields = new List<string>();
        var current = new StringBuilder(line.Length);
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes && fields.Count < maxFields - 1)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static string RemoveLineComment(string line)
    {
        // "//" inside a quoted GDT value (URLs, material paths) is not a comment.
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == '/' && !inQuotes && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
        }

        return line;
    }

    private static string CleanReferenceValue(string value) =>
        value.Trim()
            .Trim(';')
            .Trim()
            .Trim('"', '\'', '<', '>')
            .Trim();

    private static bool LooksLikeFileReference(string value) =>
        value.Contains('/') || value.Contains('\\') || Path.HasExtension(value);

    private static string ProjectPrefix(string projectName) =>
        projectName.Length >= 2 ? projectName[..2] : projectName;

    private static void Add(
        ProjectAnalysisResult result,
        string status,
        string check,
        string detail,
        string filePath = "",
        int lineNumber = 0)
    {
        result.Issues.Add(new ProjectAnalysisIssue
        {
            Status = status,
            Check = check,
            Detail = detail,
            FilePath = filePath,
            LineNumber = lineNumber,
        });
    }

    [GeneratedRegex(@"^\s*#using\s+(?<path>[^;]+);?", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUsingRegex();

    [GeneratedRegex(@"^\s*#include\s+(?<path>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneHashIncludeRegex();

    [GeneratedRegex("^\"(?<name>[^\"]+)\"\\s*\\(\\s*\"(?<type>[^\"]+)\"\\s*\\)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyGdtAssetRegex();
}
