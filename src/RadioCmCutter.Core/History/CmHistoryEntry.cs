namespace RadioCmCutter.Core.History;

/// <summary>
/// ユーザーが確定した1区間分の音響指紋。CMとして確定した区間は今後の検出で一致度を上げる材料に、
/// 「検出されたがCMではないと判断された」区間は今後の検出で一致度を下げる材料に使う。
/// フレーム単位（0.5秒ごと）で保存するため、CMの一部（BGM部分のみ等）が別のCMと共通していても
/// 部分一致として検出できる。
/// </summary>
public sealed class CmHistoryEntry
{
    public required string Id { get; init; }
    public required bool IsConfirmedCm { get; init; }
    public required List<float[]> FrameVectors { get; init; }
    public required DateTime SavedAtUtc { get; init; }

    /// <summary>参考情報（どのファイルから確定されたか）。一致判定には使わない。</summary>
    public string? SourceFileName { get; init; }
}
