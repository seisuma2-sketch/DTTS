namespace DTTS.Core.Models;

/// <summary>ルール設定の組み込みプリセット。</summary>
public enum RulePreset
{
    /// <summary>初心者用：基本のルールだけ（革命・8切り・スート縛り・階段・カード交換）。</summary>
    Beginner = 0,
    /// <summary>中級者用：定番のルール＋人気の特殊効果（11バック・スペ3返し・都落ち・7渡し・10捨てなど）。</summary>
    Intermediate,
    /// <summary>完全上級者：実装されているすべてのローカルルールを有効にしたフル構成。</summary>
    Expert,
}

/// <summary>
/// ルール設定のプリセット。ローカルルールをコードに固定しないという方針に沿い、プリセットも
/// <see cref="RuleSettings"/> の値の組み合わせとして作る（UI はここで作った設定をそのまま編集・保存する）。
/// </summary>
public static class RulePresets
{
    /// <summary>プリセットの表示名。</summary>
    public static string DisplayName(RulePreset preset) => preset switch
    {
        RulePreset.Beginner => "初心者用",
        RulePreset.Intermediate => "中級者用",
        RulePreset.Expert => "完全上級者（全ルールあり）",
        _ => preset.ToString(),
    };

    /// <summary>プリセットのルール設定を新しく作る。ラウンド数など進行の設定は <paramref name="keepProgressFrom"/> から引き継ぐ。</summary>
    public static RuleSettings Create(RulePreset preset, RuleSettings? keepProgressFrom = null)
    {
        var r = RuleSettings.CreateDefault();
        switch (preset)
        {
            case RulePreset.Beginner:
                ApplyBeginner(r);
                break;
            case RulePreset.Intermediate:
                ApplyIntermediate(r);
                break;
            case RulePreset.Expert:
                ApplyExpert(r);
                break;
        }
        if (keepProgressFrom is not null)
        {
            r.Progress.RoundCount = keepProgressFrom.Progress.RoundCount;
        }
        r.Normalize();
        return r;
    }

    /// <summary>革命・8切り・スート縛り・階段・カード交換だけの、覚えることが少ない構成。</summary>
    private static void ApplyBeginner(RuleSettings r)
    {
        r.Revolution.Enabled = true;
        r.Revolution.CounterRevolutionEnabled = true;
        r.Flow.EightCutEnabled = true;
        r.Lock.SuitLockEnabled = true;
        r.Sequence.Enabled = true;
        r.Exchange.Enabled = true;

        r.ElevenBack.Enabled = false;
        r.Joker.SpadeThreeReturnEnabled = false;
        r.RankChange.MiyakoOchiEnabled = false;
        r.ForbiddenFinish.Enabled = false; // 「最後に出せない札」を覚えなくてよい
    }

    /// <summary>標準（定番のルール）に、人気の特殊効果を足した構成。</summary>
    private static void ApplyIntermediate(RuleSettings r)
    {
        // 既定値で有効：革命・8切り・スート縛り・階段・カード交換・11バック・スペ3返し・都落ち・禁止あがり
        r.Special.SevenPassEnabled = true;
        r.Special.TenDiscardEnabled = true;
        r.SkipReverse.FiveSkipEnabled = true;
        r.SkipReverse.NineReverseEnabled = true;
    }

    /// <summary>すべての ON/OFF ルールを有効にし、選択式は「いちばん多くのことが起きる」値にしたフル構成。</summary>
    private static void ApplyExpert(RuleSettings r)
    {
        var rev = r.Revolution;
        rev.Enabled = true;
        rev.SequenceRevolutionEnabled = true;
        rev.CounterRevolutionEnabled = true;
        rev.JokerCountsForRevolution = true;
        rev.RevolutionIsOptional = true;
        rev.CoupEnabled = true;
        rev.OmenEnabled = true;
        rev.NanasanEnabled = true;

        var seq = r.Sequence;
        seq.Enabled = true;
        seq.AllowJokerInSequence = true;
        seq.AllowTwoInSequence = true;
        seq.EffectsInSequence = true;
        seq.EmperorEnabled = true;
        seq.EmperorCausesRevolution = true;

        var lk = r.Lock;
        lk.SuitLockEnabled = true;
        lk.NumberLockEnabled = true;
        lk.StrictLockEnabled = true;
        lk.PartialSuitLockEnabled = true;

        var flow = r.Flow;
        flow.EightCutEnabled = true;
        flow.FourStopEnabled = true;
        flow.SandstormEnabled = true;
        flow.AmbulanceEnabled = true;
        flow.RokurokubiEnabled = true;

        r.ElevenBack.Enabled = true;
        r.ElevenBack.Duration = Enums.ElevenBackDuration.Persistent; // 強化11バック

        var sr = r.SkipReverse;
        sr.FiveSkipEnabled = true;
        sr.ThirteenSkipEnabled = true;
        sr.NineReverseEnabled = true;
        sr.TwelveReverseEnabled = true;

        var sp = r.Special;
        sp.SevenPassEnabled = true;
        sp.TenDiscardEnabled = true;
        sp.TwelveBomberEnabled = true;
        sp.DownNumberEnabled = true;

        r.Joker.JokerCount = 2;
        r.Joker.SpadeThreeReturnEnabled = true;
        r.Joker.JokerTriggersEffects = true;

        r.RankChange.MiyakoOchiEnabled = true;
        r.RankChange.GekokujoEnabled = true;

        var ff = r.ForbiddenFinish;
        ff.Enabled = true;
        ff.ForbidStrongestNumber = true;
        ff.StrongestFollowsReversal = true;
        ff.ForbidEightCut = true;
        ff.ForbidJoker = true;
        ff.ForbidSpadeThree = true;
        ff.ForbidTwelveBomber = true;
        ff.ForbidElevenBack = true;
        ff.ForbidOtherFlushEffects = true;
        ff.ForbidRevolution = true;

        var ex = r.Exchange;
        ex.Enabled = true;
        ex.LinkedToMiyakoOchi = true;
        ex.PoorCanChooseCards = true;
        ex.JokerProtected = true;
        ex.TenpenChiiEnabled = true;

        var pr = r.Progress;
        pr.DiamondThreeStarts = true;
        pr.SeatShuffleEachRound = true;
        pr.PassLocksUntilFlush = true;
        pr.KeepFieldOnPlayerFinish = true;
        pr.ExtraRoundOnTie = true;
    }
}
