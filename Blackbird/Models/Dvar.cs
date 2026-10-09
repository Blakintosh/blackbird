using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackbird.Models;

public enum DvarType
{
    Bool,
    Int,
    String
}

public partial class Dvar : ObservableObject
{
    public string Name { get; }
    public string Description { get; }
    public DvarType Type { get; }
    public int MinValue { get; }
    public int MaxValue { get; }
    public bool IsCmd { get; }
    public string Kind { get; }

    [ObservableProperty]
    private object? _value;

    public string ValueText
    {
        get => Value switch
        {
            bool b => b ? "true" : "false",
            null => "",
            _ => Value.ToString() ?? ""
        };
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Value = "";
                return;
            }

            Value = Type switch
            {
                DvarType.Bool => value is "true" or "True" or "1",
                DvarType.Int => int.TryParse(value, out var i) ? ClampIfBounded(i) : "",
                _ => value
            };
        }
    }

    public bool IsBool => Type == DvarType.Bool;

    public bool BoolValue
    {
        get => Value is true;
        set => Value = value;
    }

    /// <summary>True when the value would reach the command line (see <see cref="BuildCommandArgs"/>).</summary>
    public bool IsSet => Value switch
    {
        bool b => b,
        null => false,
        _ => ValueText.Length > 0 && !(Type == DvarType.Int && ValueText == "0"),
    };

    /// <summary>Placeholder for an empty value field: the accepted range, or "Not set".</summary>
    public string ValueHint => Type == DvarType.Int && MinValue < MaxValue
        ? $"{MinValue}–{MaxValue}"
        : "Not set";

    public void Clear() => Value = Type == DvarType.Bool ? false : "";

    partial void OnValueChanged(object? value)
    {
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(IsSet));
    }

    public Dvar(string name, string description, DvarType type, int minValue = 0, int maxValue = 0, bool isCmd = false, string? kind = null)
    {
        Name = name;
        Description = description;
        Type = type;
        MinValue = minValue;
        MaxValue = maxValue;
        IsCmd = isCmd;
        Kind = kind ?? (isCmd ? "cmd" : type.ToString().ToLowerInvariant());
    }

    private static readonly Lazy<Dvar[]> DefaultsLoader = new(LoadDefaults, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dvar[] SortedManualDefaults = SortDefaults(ManualDefaults());

    /// <summary>
    /// The full dvar catalog. The first access unzips and parses the embedded CSVs;
    /// if <see cref="WarmDefaultsInBackground"/> already started that, it waits for it.
    /// </summary>
    public static Dvar[] Defaults => DefaultsLoader.Value;

    /// <summary>Starts loading <see cref="Defaults"/> on the thread pool, if it hasn't been loaded yet.</summary>
    public static void WarmDefaultsInBackground()
    {
        if (!DefaultsLoader.IsValueCreated)
            _ = Task.Run(() => DefaultsLoader.Value);
    }

    /// <summary>
    /// Build command-line args from a saved dvar dictionary, reusable outside the dialog.
    /// </summary>
    public static List<string> BuildCommandArgs(Dictionary<string, string> savedValues)
    {
        var args = new List<string>();
        foreach (var def in CatalogFor(savedValues))
        {
            if (!savedValues.TryGetValue(def.Name, out var raw) || string.IsNullOrEmpty(raw))
                continue;

            string value;
            if (def.Type == DvarType.Bool)
            {
                bool enabled = raw is "true" or "True" or "1";
                if (!enabled) continue;
                value = "1";
            }
            else
            {
                if (def.Type == DvarType.Int && raw == "0") continue;
                value = raw;
            }

            if (!def.IsCmd)
            {
                args.Add("+set");
                args.Add(def.Name);
            }
            else
            {
                args.Add($"+{def.Name}");
            }
            args.Add(value);
        }
        return args;
    }

    /// <summary>
    /// The catalog entries <see cref="BuildCommandArgs"/> has to walk. When every set value
    /// names a manual default (the Dev/Ship presets do) and the full catalog isn't loaded yet,
    /// the manual defaults alone give the same args without parsing the CSVs.
    /// </summary>
    private static Dvar[] CatalogFor(Dictionary<string, string> savedValues)
    {
        if (DefaultsLoader.IsValueCreated)
            return DefaultsLoader.Value;

        // Catalog names are unique ignoring case, so under these comparers a key that
        // matches a manual name can't also match any other catalog entry.
        var comparer = savedValues.Comparer;
        if (!ReferenceEquals(comparer, EqualityComparer<string>.Default)
            && !ReferenceEquals(comparer, StringComparer.Ordinal)
            && !ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase))
        {
            return Defaults;
        }

        foreach (var (name, raw) in savedValues)
        {
            if (string.IsNullOrEmpty(raw))
                continue;
            if (!SortedManualDefaults.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Defaults;
        }

        return SortedManualDefaults;
    }

    private int ClampIfBounded(int value)
    {
        return MinValue < MaxValue ? Math.Clamp(value, MinValue, MaxValue) : value;
    }

    private static Dvar[] LoadDefaults()
    {
        var dvars = new Dictionary<string, Dvar>(StringComparer.OrdinalIgnoreCase);
        var manualDefaults = ManualDefaults();
        var protectedNames = new HashSet<string>(manualDefaults.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var dvar in manualDefaults)
            dvars[dvar.Name] = dvar;

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Blackbird.Data.Bo3_Dvars_Set_Via_Gsc_Csc_Only.zip");
        if (stream is null)
            return SortDefaults(dvars.Values);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            var kind = Path.GetFileNameWithoutExtension(entry.FullName).Replace("dvar_", "", StringComparison.OrdinalIgnoreCase);
            var type = kind switch
            {
                "bool" => DvarType.Bool,
                "int" => DvarType.Int,
                _ => DvarType.String
            };

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!reader.EndOfStream)
            {
                var fields = ParseCsvLine(reader.ReadLine() ?? "");
                if (fields.Count == 0 || string.IsNullOrWhiteSpace(fields[0]))
                    continue;

                var name = fields[0].Trim();
                var description = PickDescription(fields);
                var candidate = new Dvar(name, description, type, kind: kind);

                if (protectedNames.Contains(name))
                    continue;

                if (!dvars.TryGetValue(name, out var existing) || BetterDescription(candidate.Description, existing.Description))
                    dvars[name] = candidate;
            }
        }

        return SortDefaults(dvars.Values);
    }

    private static Dvar[] ManualDefaults() =>
    [
        new("ai_disableSpawn", "Disable AI from spawning", DvarType.Bool),
        new("developer", "Run developer mode", DvarType.Int, 0, 2),
        new("g_password", "Password for your server", DvarType.String),
        new("logfile", "Console log information written to current fs_game", DvarType.Int, 0, 2),
        new("scr_mod_enable_devblock", "Developer blocks are executed in mods", DvarType.Bool),
        new("connect", "Connect to a specific server", DvarType.String, isCmd: true),
        new("set_gametype", "Set a gametype to load with map", DvarType.String, isCmd: true),
        new("com_clientfieldsDebug", "Enable client fields debug output", DvarType.Bool),
        new("splitscreen", "Enable splitscreen", DvarType.Bool),
        new("splitscreen_playerCount", "Allocate the number of instances for splitscreen", DvarType.Int, 0, 2),
    ];

    private static Dvar[] SortDefaults(IEnumerable<Dvar> dvars) =>
        dvars.OrderBy(d => d.IsCmd ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool BetterDescription(string candidate, string existing) =>
        !string.IsNullOrWhiteSpace(candidate)
        && (string.IsNullOrWhiteSpace(existing) || candidate.Length > existing.Length);

    private static string PickDescription(IReadOnlyList<string> fields)
    {
        return fields.Skip(1)
            .Select(f => f.Trim())
            .Where(IsUsefulDescription)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault() ?? "";
    }

    private static bool IsUsefulDescription(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Any(char.IsControl) || value.Contains('\uFFFD'))
            return false;
        return value.Any(char.IsLetter);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
