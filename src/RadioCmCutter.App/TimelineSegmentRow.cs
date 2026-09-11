using RadioCmCutter.Core.Ffmpeg;
using RadioCmCutter.Core.Models;

namespace RadioCmCutter.App;

/// <summary>
/// 画面表示用: ファイル全体を「CM候補区間」と「その間（検出されなかった区間）」に途切れなく分割した1行。
/// CM候補は検出結果由来でCutEnabledの初期値がON、間の区間は検出されていないためOFFで始まる
/// （ユーザーがONにすれば、検出漏れの区間を手動でカット対象に追加できる）。
/// </summary>
public sealed class TimelineSegmentRow
{
    public required AudioSegment Segment { get; init; }
    public bool CutEnabled { get; set; }
    public double Confidence { get; init; }
    public int RepeatCount { get; init; }
    public required bool IsDetectedCandidate { get; init; }
    public DetectionReason? Reason { get; init; }

    public string Kind => Reason switch
    {
        DetectionReason.RepeatedContent => "繰り返し検出",
        DetectionReason.AcousticTransitionCmLength => "音量/音色変化(CM尺)",
        DetectionReason.AcousticTransitionLong => "音量/音色変化(長尺・音楽?)",
        _ => "（検出外）",
    };

    /// <summary>検出されたCM候補区間の一覧から、ファイル全体を途切れなくカバーする表示行を作る。</summary>
    public static List<TimelineSegmentRow> Build(TimeSpan totalDuration, IReadOnlyList<CmCandidate> candidates)
    {
        var candidateRows = candidates.Select(c => new TimelineSegmentRow
        {
            Segment = c.Segment,
            CutEnabled = c.CutEnabled,
            Confidence = c.Confidence,
            RepeatCount = c.RepeatCount,
            IsDetectedCandidate = true,
            Reason = c.Reason,
        });

        var gapSegments = AudioCutter.ComputeKeptSegments(totalDuration, candidates.Select(c => c.Segment));
        var gapRows = gapSegments.Select(s => new TimelineSegmentRow
        {
            Segment = s,
            CutEnabled = false,
            Confidence = 0,
            RepeatCount = 0,
            IsDetectedCandidate = false,
        });

        return candidateRows.Concat(gapRows).OrderBy(r => r.Segment.Start).ToList();
    }
}
