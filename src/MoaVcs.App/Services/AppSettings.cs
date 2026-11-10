using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaVcs.Core;

namespace MoaVcs.App.Services;

public sealed record RecentRepository(string Path, VcsKind VcsKind);

public enum ExternalToolKind { Diff, Blame }

public sealed class ExternalToolSettings
{
    public bool UseExternal { get; set; }
    public string ExecutablePath { get; set; } = "";
    /// <summary>One argument template per entry. Entries are never parsed as shell command text.</summary>
    public List<string> ArgumentTemplates { get; set; } = [];
}

public sealed class ExplorerMenuSettings
{
    public bool ShowCommitAtTopLevel { get; set; } = true;
    public bool ShowUpdateAtTopLevel { get; set; } = true;
}

public sealed class AppSettings
{
    public List<RecentRepository> RecentRepositories { get; set; } = [];
    public Dictionary<VcsKind, string> ExecutablePaths { get; set; } = [];
    public Dictionary<ExternalToolKind, ExternalToolSettings> ExternalTools { get; set; } = [];
    public ExplorerMenuSettings ExplorerMenu { get; set; } = new();
}

/// <summary>Persists per-user preferences without requiring administrator access.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    public string FilePath { get; }
    public string? LoadWarning { get; private set; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoaVcs", "settings.json");
    }

    public AppSettings Load()
    {
        LoadWarning = null;
        if (!File.Exists(FilePath)) return new AppSettings();

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var loaded = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            if (loaded is null) throw new JsonException("설정 파일 내용이 비어 있습니다.");
            loaded.RecentRepositories ??= [];
            loaded.ExecutablePaths ??= [];
            loaded.ExternalTools ??= [];
            loaded.ExplorerMenu ??= new ExplorerMenuSettings();
            foreach (var kind in Enum.GetValues<ExternalToolKind>())
            {
                if (!loaded.ExternalTools.TryGetValue(kind, out var tool) || tool is null)
                    loaded.ExternalTools[kind] = new ExternalToolSettings();
                else
                {
                    tool.ExecutablePath ??= "";
                    tool.ArgumentTemplates ??= [];
                }
            }
            loaded.RecentRepositories = loaded.RecentRepositories
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path))
                .DistinctBy(r => Path.GetFullPath(r.Path), StringComparer.OrdinalIgnoreCase)
                .Take(20).ToList();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Keep the original intact so the user can recover hand-edited settings.
            LoadWarning = $"설정 파일을 읽지 못해 기본 설정으로 시작합니다. 파일을 확인하세요: {ex.Message}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))
            ?? throw new IOException("설정 파일 경로를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);
        var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public void SaveExplorerMenu(ExplorerMenuSettings explorerMenu)
    {
        ArgumentNullException.ThrowIfNull(explorerMenu);
        var settings = Load();
        if (LoadWarning is { } warning)
            throw new IOException("설정 파일을 읽을 수 없어 탐색기 메뉴 설정을 저장하지 않았습니다. " + warning);
        settings.ExplorerMenu = new ExplorerMenuSettings
        {
            ShowCommitAtTopLevel = explorerMenu.ShowCommitAtTopLevel,
            ShowUpdateAtTopLevel = explorerMenu.ShowUpdateAtTopLevel
        };
        Save(settings);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
