namespace RadioCmCutter.Core.Models;

/// <summary>1ファイルに対するCM検出結果。</summary>
public sealed class DetectionResult
{
    public required string SourceFilePath { get; set; }

    public required TimeSpan TotalDuration { get; set; }

    public List<CmCandidate> Candidates { get; set; } = [];
}
