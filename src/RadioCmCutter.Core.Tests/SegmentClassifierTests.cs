using RadioCmCutter.Core.Detection;
using RadioCmCutter.Core.Models;
using Xunit;

namespace RadioCmCutter.Core.Tests;

public class SegmentClassifierTests
{
    private const double FrameSeconds = FeatureExtractor.FrameSeconds; // 0.5秒

    [Fact]
    public void Classify_ShortRepeatRunInsideLongSegment_IsStillDetectedAsRepeatedContent()
    {
        // 40秒(80フレーム)の区間に、10秒(20フレーム)だけ繰り返し一致がある状態。
        // 「区間内の一致フレーム割合」で判定すると 0.25 で落ちてしまうが、
        // 「最小run長を満たす連続一致があるか」で判定するため検出されるべき。
        var frames = BuildFrames(120);
        var hitCount = new int[frames.Count];
        var hitScoreSum = new double[frames.Count];
        for (var i = 10; i < 30; i++)
        {
            hitCount[i] = 2;
            hitScoreSum[i] = 2 * 0.95;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(40), Boundary(60)],
            new RepeatEvidence(hitCount, hitScoreSum),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        var repeated = Assert.Single(candidates, c => c.Reason == DetectionReason.RepeatedContent);
        Assert.Equal(TimeSpan.Zero, repeated.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(40), repeated.Segment.End);
        Assert.True(repeated.CutEnabled);
        Assert.Equal(0.95, repeated.Confidence, precision: 3); // run部分のみから算出される
        Assert.Equal(2, repeated.RepeatCount);
    }

    [Fact]
    public void Classify_AdjacentWeakSegments_AreKeptSeparateWithTheirOwnLengthLabel()
    {
        // 弱い根拠（音響急変のみ）の隣接区間は結合しない。
        // 区間の間にあるのは実際に検出された切れ目であり、まとめると
        // 「どこで切れるか」という情報を失い、確認すべき行も出なくなるため。
        var frames = BuildFrames(120);

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(20), Boundary(45), Boundary(60)],
            new RepeatEvidence(new int[frames.Count], new double[frames.Count]),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, c => Assert.False(c.CutEnabled)); // 音響急変のみを根拠にカット対象とはしない
        Assert.Equal(
            [
                TimeSpan.Zero, TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45),
                TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60),
            ],
            candidates.SelectMany(c => new[] { c.Segment.Start, c.Segment.End }));

        // 各区間はそれぞれの長さでラベル付けされる（20秒・25秒は長尺、15秒はCM尺相当）
        Assert.Equal(DetectionReason.AcousticTransitionLong, candidates[0].Reason);
        Assert.Equal(DetectionReason.AcousticTransitionLong, candidates[1].Reason);
        Assert.Equal(DetectionReason.AcousticTransitionCmLength, candidates[2].Reason);
    }

    [Fact]
    public void Classify_RepeatSegmentsSplitBySpuriousBoundary_AreCoalesced()
    {
        // 1つのCMが余分な境界（20秒地点）で分断されていても、繰り返し一致が続いている限り
        // 1件の候補としてまとめる
        var frames = BuildFrames(120);
        var hitCount = new int[frames.Count];
        var hitScoreSum = new double[frames.Count];
        for (var i = 0; i < 80; i++) // 0〜40秒
        {
            hitCount[i] = 1;
            hitScoreSum[i] = 0.98;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(20), Boundary(40), Boundary(60)],
            new RepeatEvidence(hitCount, hitScoreSum),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        var repeated = Assert.Single(candidates, c => c.Reason == DetectionReason.RepeatedContent);
        Assert.Equal(TimeSpan.Zero, repeated.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(40), repeated.Segment.End);
    }

    [Fact]
    public void Classify_RepeatRunSplitByBoundaryIntoShortPieces_IsStillDetected()
    {
        // 実データで検出が失われたケースの再現: 9.2秒の繰り返しrunの中にカット位置候補が落ち、
        // 8.6秒+0.6秒に分断されていた。ComputeHits の段階で既に最小run長のふるいを
        // 通しているため、区間ごとに再度8秒を要求すると真陽性が消えてしまう。
        var frames = BuildFrames(120);
        var hitCount = new int[frames.Count];
        var hitScoreSum = new double[frames.Count];
        for (var i = 20; i < 38; i++) // 10〜19秒（9秒分）
        {
            hitCount[i] = 2;
            hitScoreSum[i] = 2 * 0.97;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(15), Boundary(30)], // 15秒でrunを分断（各区間の一致は5秒と4秒）
            new RepeatEvidence(hitCount, hitScoreSum),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        // 分断後の一致長はどちらも8秒未満だが、両側とも繰り返し由来と判定され結合されて1件になる
        // （8秒を再度要求していた旧実装では1件も検出されなかった）
        var repeated = Assert.Single(candidates, c => c.Reason == DetectionReason.RepeatedContent);
        Assert.Equal(TimeSpan.Zero, repeated.Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), repeated.Segment.End);
        Assert.True(repeated.CutEnabled);
    }

    [Fact]
    public void Classify_ScatteredSingleFrameHits_DoNotMakeSegmentRepeated()
    {
        // 散発的な単発ヒットだけでは区間全体を繰り返し扱いにしない（連続性を要求する）
        var frames = BuildFrames(120);
        var hitCount = new int[frames.Count];
        var hitScoreSum = new double[frames.Count];
        for (var i = 10; i < 60; i += 5) // 1フレームずつ飛び飛び
        {
            hitCount[i] = 1;
            hitScoreSum[i] = 0.95;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(30), Boundary(60)],
            new RepeatEvidence(hitCount, hitScoreSum),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        Assert.DoesNotContain(candidates, c => c.Reason == DetectionReason.RepeatedContent);
    }

    [Fact]
    public void Classify_HistoryMatchUsesShorterMinRunThanRepeat()
    {
        // 履歴一致は、CMのBGM・ジングル部分のみの部分一致（既定3秒＝6フレーム）を拾えること
        var frames = BuildFrames(60);
        var confirmed = new double[frames.Count];
        var rejected = new double[frames.Count];
        for (var i = 4; i < 12; i++) // 4秒分
        {
            confirmed[i] = 0.97;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(20), Boundary(30)],
            new RepeatEvidence(new int[frames.Count], new double[frames.Count]),
            new HistoryMatchScores(confirmed, rejected),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        var match = Assert.Single(candidates, c => c.Reason == DetectionReason.HistoryMatch);
        Assert.True(match.CutEnabled);
        Assert.Equal(0.97, match.Confidence, precision: 3);
    }

    [Fact]
    public void Classify_RejectedHistoryScoreHigher_DoesNotMatch()
    {
        // 確定「非CM」側の方が近い場合は一致としない（過去にユーザーが否定したパターンの抑制）
        var frames = BuildFrames(60);
        var confirmed = new double[frames.Count];
        var rejected = new double[frames.Count];
        for (var i = 4; i < 20; i++)
        {
            confirmed[i] = 0.95;
            rejected[i] = 0.99;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(20), Boundary(30)],
            new RepeatEvidence(new int[frames.Count], new double[frames.Count]),
            new HistoryMatchScores(confirmed, rejected),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        Assert.DoesNotContain(candidates, c => c.Reason == DetectionReason.HistoryMatch);
    }

    [Fact]
    public void Classify_WeakSegmentOutsideReviewableLengthRange_IsNotEmitted()
    {
        // 裏付けのない区間のうち、長すぎるもの（既定300秒超＝番組本編と考えられる）は候補にしない。
        // 一覧が埋まって波形が塗り潰され、確定履歴が未確認の本編で汚染されるのを防ぐため。
        var frames = BuildFrames(1400); // 700秒
        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(700)],
            new RepeatEvidence(new int[frames.Count], new double[frames.Count]),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        Assert.Empty(candidates);
    }

    [Fact]
    public void Classify_RepeatedSegmentIsEmittedRegardlessOfLength()
    {
        // 繰り返し一致は裏付けのある根拠なので、長さの確認レンジ（8〜300秒）外でも候補として出す
        var frames = BuildFrames(1400); // 700秒
        var hitCount = new int[frames.Count];
        var hitScoreSum = new double[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            hitCount[i] = 1;
            hitScoreSum[i] = 0.99;
        }

        var candidates = SegmentClassifier.Classify(
            frames,
            [Boundary(0), Boundary(700)],
            new RepeatEvidence(hitCount, hitScoreSum),
            HistoryMatchScores.Empty(frames.Count),
            new HistoryMatchOptions(),
            new SegmentClassificationOptions());

        var candidate = Assert.Single(candidates);
        Assert.Equal(DetectionReason.RepeatedContent, candidate.Reason);
        Assert.Equal(TimeSpan.FromSeconds(700), candidate.Segment.End);
    }

    private static BoundaryPoint Boundary(double seconds) => new(TimeSpan.FromSeconds(seconds), 1.0);

    private static List<FrameFeatures> BuildFrames(int totalFrames)
    {
        var frames = new List<FrameFeatures>(totalFrames);
        for (var i = 0; i < totalFrames; i++)
        {
            var start = TimeSpan.FromSeconds(i * FrameSeconds);
            frames.Add(new FrameFeatures
            {
                Start = start,
                End = start + TimeSpan.FromSeconds(FrameSeconds),
                Vector = [1f],
                SpectralVector = [1f],
            });
        }
        return frames;
    }
}
