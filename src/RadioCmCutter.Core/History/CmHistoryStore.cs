using System.Text.Json;

namespace RadioCmCutter.Core.History;

/// <summary>
/// ユーザーが確定したCM／非CM区間の音響指紋を、番組を問わずグローバルに1つのファイルへ蓄積する。
/// 蓄積されるほど、同じCM（他番組での再放送・同一スポンサーの別番組での使用等）を検出しやすくなる想定。
/// </summary>
public sealed class CmHistoryStore
{
    private readonly List<CmHistoryEntry> _entries;

    public IReadOnlyList<CmHistoryEntry> ConfirmedCmEntries => _entries.Where(e => e.IsConfirmedCm).ToList();
    public IReadOnlyList<CmHistoryEntry> RejectedEntries => _entries.Where(e => !e.IsConfirmedCm).ToList();

    private CmHistoryStore(List<CmHistoryEntry> entries)
    {
        _entries = entries;
    }

    public static string GetDefaultFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioCmCutter");
        return Path.Combine(dir, "cm_history.json");
    }

    public static CmHistoryStore Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new CmHistoryStore([]);
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var entries = JsonSerializer.Deserialize<List<CmHistoryEntry>>(json) ?? [];
            return new CmHistoryStore(entries);
        }
        catch (JsonException)
        {
            // 壊れた履歴ファイルは無視して空から再開する（検出処理自体は継続できる方が重要）
            return new CmHistoryStore([]);
        }
    }

    public void Add(CmHistoryEntry entry)
    {
        _entries.Add(entry);
    }

    public void Save(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_entries);
        File.WriteAllText(filePath, json);
    }
}
