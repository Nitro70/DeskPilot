using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPilot.Core.Settings;

/// <summary>Loads and saves settings.json atomically. Thread-safe; Current is replaced as a whole on save.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _path;
    private AppSettings _current;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _current = LoadFromDisk(_path, out var loadError);
        LoadError = loadError;
    }

    public string FilePath => _path;

    /// <summary>Non-null when settings.json existed but could not be parsed (defaults were used; the bad file was backed up).</summary>
    public string? LoadError { get; }

    public event Action<AppSettings>? Changed;

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Deep copy for editing in a settings dialog; commit with Save().</summary>
    public AppSettings CloneCurrent() => Clone(Current);

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            _current = settings;
        }
        Changed?.Invoke(settings);
    }

    public void Update(Action<AppSettings> mutate)
    {
        var copy = CloneCurrent();
        mutate(copy);
        Save(copy);
    }

    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        var claude = ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
        s.Profiles.Add(claude);
        s.ActiveProfileId = claude.Id;
        return s;
    }

    private static AppSettings LoadFromDisk(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return CreateDefault();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? CreateDefault();
            Normalize(s);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = $"settings.json could not be read ({ex.Message}); defaults were loaded and the old file was kept as settings.json.bad";
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return CreateDefault();
        }
    }

    /// <summary>Repairs null collections / missing profiles after deserialization or editing.</summary>
    public static void Normalize(AppSettings s)
    {
        s.Profiles ??= new();
        s.Vault ??= new();
        s.Safety ??= new();
        s.Screen ??= new();
        s.Ui ??= new();
        s.Prompt ??= new();
        s.Vault.IncludeExtensions ??= new();
        s.Vault.ExcludeFolders ??= new();
        s.Safety.BlockedProcesses ??= new();
        s.Safety.ElevationTextPatterns ??= new();
        s.Safety.ElevatedLaunchTargets ??= new();
        foreach (var p in s.Profiles)
        {
            p.ExtraHeaders ??= new();
            p.ExtraEnv ??= new();
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
        }
        if (s.Profiles.Count == 0)
        {
            var claude = ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
            s.Profiles.Add(claude);
        }
        if (s.Profiles.All(p => p.Id != s.ActiveProfileId)) s.ActiveProfileId = s.Profiles[0].Id;
        s.Screen.MaxImageWidth = Math.Clamp(s.Screen.MaxImageWidth, 320, 3840);
        s.Screen.MaxImageHeight = Math.Clamp(s.Screen.MaxImageHeight, 240, 2160);
        s.Screen.JpegQuality = Math.Clamp(s.Screen.JpegQuality, 20, 100);
        s.Screen.ScreenshotsToKeep = Math.Clamp(s.Screen.ScreenshotsToKeep, 1, 50);
        s.Safety.MaxStepsPerTurn = Math.Clamp(s.Safety.MaxStepsPerTurn, 1, 1000);
    }
}
