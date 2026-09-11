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

    /// <summary>直前フレームからの対数帯域エネルギーの変化量（L2ノルム、正規化前の絶対値）。
    /// 会話↔曲のような音色の急変を検出するために使う（0は変化なし、先頭フレームは常に0）。</summary>
    public required double SpectralChangeMagnitude { get; init; }
}
