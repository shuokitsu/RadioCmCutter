namespace RadioCmCutter.Core.Models;

/// <summary>検出されたCM候補区間。</summary>
public sealed class CmCandidate
{
    public required AudioSegment Segment { get; set; }

    /// <summary>検出の確信度（0.0〜1.0）。繰り返し検出での一致度をもとに算出。</summary>
    public required double Confidence { get; set; }

    /// <summary>何回の繰り返しとして検出されたか（ファイル内・ファイル間合計）。</summary>
    public int RepeatCount { get; set; }

    /// <summary>この区間をカット対象とするか（GUI上でユーザーがON/OFF切替可能）。デフォルトはON。</summary>
    public bool CutEnabled { get; set; } = true;
}
