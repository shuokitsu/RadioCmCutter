using System.IO;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.App;

/// <summary>ファイル一覧(ListBox)に表示するための、検出結果1ファイル分のラッパー。</summary>
public sealed class FileResultItem(DetectionResult result)
{
    public DetectionResult Result { get; } = result;

    public string FilePath => Result.SourceFilePath;

    /// <summary>ファイル全体を途切れなく分割した表示行（CM候補＋その間）。選択切替後も編集内容を保持するため保存しておく。</summary>
    public List<TimelineSegmentRow> TimelineRows { get; } = TimelineSegmentRow.Build(result.TotalDuration, result.Candidates);

    public override string ToString()
    {
        var fileName = Path.GetFileName(FilePath);
        var enabledCount = TimelineRows.Count(r => r.CutEnabled);
        return $"{fileName}  （CM候補 {Result.Candidates.Count}件 / カット対象 {enabledCount}件）";
    }
}
