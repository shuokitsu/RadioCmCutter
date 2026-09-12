using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadioCmCutter.Core.Evaluation;

/// <summary>
/// ユーザーが確認・修正した「正解の区切り」。検出精度を測るためだけに使い、
/// 検出処理には絶対に渡さない（渡すと答えを見ながら答案を書くことになり、精度が測れなくなる）。
///
/// 確定履歴（<see cref="History.CmHistoryStore"/>）とは役割が逆である点に注意:
/// 確定履歴は検出の材料として使う入力、こちらは検出結果を採点するための基準。
/// </summary>
public sealed class BoundaryGroundTruth
{
    /// <summary>元の音声ファイル名（参考情報。突き合わせはファイルパスで行う）。</summary>
    public string? SourceFileName { get; init; }

    public double TotalDurationSeconds { get; init; }

    /// <summary>区切りの時刻（秒）。ファイルの先頭・末尾は含めない。</summary>
    public List<double> BoundarySeconds { get; init; } = [];

    /// <summary>実際に確認し終えた範囲（秒）。null ならファイル全体を確認済みとみなす。
    ///
    /// これを指定しないと適合率が意味を持たない。ファイルの一部しか確認していない状態で
    /// 全体を採点すると、未確認部分の検出はすべて「外れ」と数えられてしまうため
    /// （実際には正解が書かれていないだけで、当たっているかもしれない）。
    /// 少しずつ確認を進める場合は、確認できた範囲をここに書いて採点対象を絞る。</summary>
    public double? AnnotatedRangeStartSeconds { get; set; }

    public double? AnnotatedRangeEndSeconds { get; set; }

    /// <summary>後から見返したときのための覚え書き（「1:20-6:18は1曲」など）。</summary>
    public string? Note { get; set; }

    public DateTime SavedAtUtc { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true, // 人が中身を確認・手直しできるようにする
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>音声ファイルに対応する正解ファイルのパス（同じ場所に並べて置く）。
    /// 隠れた場所に保存すると、ユーザーが中身を確認したり手で直したりできないため。</summary>
    public static string GetDefaultFilePath(string audioFilePath) => audioFilePath + ".boundaries.json";

    public static BoundaryGroundTruth? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<BoundaryGroundTruth>(File.ReadAllText(filePath), SerializerOptions);
        }
        catch (JsonException)
        {
            return null; // 壊れていたら「正解なし」として扱う（検出や画面操作は止めない）
        }
    }

    public void Save(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(this, SerializerOptions));
    }
}
