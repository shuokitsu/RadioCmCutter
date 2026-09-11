namespace RadioCmCutter.Core.Detection;

/// <summary>1フレーム分の音響特徴。繰り返し検出（コサイン類似度）に使う。</summary>
public sealed class FrameFeatures
{
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }

    /// <summary>対数帯域エネルギーをL2正規化したベクトル。</summary>
    public required float[] Vector { get; init; }

    /// <summary>フレーム内のRMS（0〜1程度）。音量境界の補助判定に使う。</summary>
    public required double Rms { get; init; }
}
