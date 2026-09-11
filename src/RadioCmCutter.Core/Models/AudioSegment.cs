namespace RadioCmCutter.Core.Models;

/// <summary>音声内の時間区間（開始・終了はファイル先頭からの経過時間）。</summary>
public readonly record struct AudioSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}
