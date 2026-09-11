namespace RadioCmCutter.Core.Models;

/// <summary>候補がどの手がかりで検出されたか。画面上で検出理由として表示する。</summary>
public enum DetectionReason
{
    /// <summary>同一音源の繰り返し出現（最も信頼度が高い）。</summary>
    RepeatedContent,

    /// <summary>音量・音色の急変＋区間の長さがCMスポット尺（15秒の倍数付近）に近い。</summary>
    AcousticTransitionCmLength,

    /// <summary>音量・音色の急変だが尺がCMスポットより長く、番組内の音楽（本編）である可能性がある。</summary>
    AcousticTransitionLong,

    /// <summary>過去にユーザーが「CMである」と確定した区間と音響的に類似している。</summary>
    HistoryMatch,
}

/// <summary>検出されたCM候補区間。</summary>
public sealed class CmCandidate
{
    public required AudioSegment Segment { get; set; }

    /// <summary>検出の確信度（0.0〜1.0）。繰り返し検出での一致度をもとに算出。</summary>
    public required double Confidence { get; set; }

    /// <summary>何回の繰り返しとして検出されたか（ファイル内・ファイル間合計）。</summary>
    public int RepeatCount { get; set; }

    /// <summary>検出理由（画面表示用）。</summary>
    public required DetectionReason Reason { get; set; }

    /// <summary>この区間をカット対象とするか（GUI上でユーザーがON/OFF切替可能）。
    /// 裏付けのある根拠（繰り返し検出・過去の確定履歴一致）はデフォルトON。
    /// 音響急変＋長さ一致のみの候補は、実データ検証で本編（曲・コーナー転換等）の誤検出が
    /// 多かったため、CM尺相当・長尺のいずれもデフォルトOFF（ユーザーが手動でONにする）。</summary>
    public bool CutEnabled { get; set; } = true;
}
