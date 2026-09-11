namespace RadioCmCutter.Core.Detection;

/// <summary>1フレーム分の音響特徴。</summary>
public sealed class FrameFeatures
{
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }

    /// <summary>対数帯域エネルギー＋直前フレームからの変化量（デルタ）を連結しL2正規化したベクトル。
    /// 繰り返し検出・履歴照合のコサイン類似度に使う。デルタを含めるのは、一定のトーンやノイズのように
    /// 時間変化しない音が「自分自身との繰り返し」と誤判定されるのを防ぐため。</summary>
    public required float[] Vector { get; init; }

    /// <summary>そのフレーム単体の対数帯域エネルギーをL2正規化したベクトル（スペクトル形状）。
    /// カット位置候補の検出で、境界の前後を窓で平均して比較するのに使う
    /// （デルタ成分は窓内で平均するとほぼ打ち消し合うため、こちらを使う）。</summary>
    public required float[] SpectralVector { get; init; }
}
