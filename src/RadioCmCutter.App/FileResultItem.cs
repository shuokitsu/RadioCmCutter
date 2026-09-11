using System.IO;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.App;

/// <summary>ファイル一覧(ListBox)に表示するための、検出結果1ファイル分のラッパー。</summary>
public sealed class FileResultItem(DetectionResult result)
{
    public DetectionResult Result { get; } = result;

    public string FilePath => Result.SourceFilePath;

    public override string ToString()
    {
        var fileName = Path.GetFileName(FilePath);
        var enabledCount = Result.Candidates.Count(c => c.CutEnabled);
        return $"{fileName}  （CM候補 {Result.Candidates.Count}件 / カット対象 {enabledCount}件）";
    }
}
