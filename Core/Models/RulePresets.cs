namespace DTTS.Core.Models;

/// <summary>ルール設定のプリセット種別。</summary>
public enum RulePreset
{
    /// <summary>標準（既定値）。</summary>
    Standard = 0,
    /// <summary>シンプル：基本の大富豪（革命・8切り・カード交換のみ）。</summary>
    Simple,
    /// <summary>全部盛り：ローカルルールをほぼすべて有効にする。</summary>
    Chaos,
}

/// <summary>
/// ルール設定のプリセット。ローカルルールをコードに固定しないという方針に沿い、プリセットも
/// <see cref="RuleSettings"/> の値の組み合わせとして作る（UI はここで作った設定をそのまま編集・保存する）。
/// </summary>
public static class RulePresets
{
    /// <summary>プリセットのルール設定を新しく作る。ラウンド数など進行の設定は <paramref name="keepProgressFrom"/> から引き継ぐ。</summary>
    public static RuleSettings Create(RulePreset preset, RuleSettings? keepProgressFrom = null)
    {
        var r = RuleSettings.CreateDefault();
        switch (preset)
        {
            case RulePreset.Simple:
                ApplySimple(r);
                break;
            case RulePreset.Chaos:
                ApplyChaos(r);
                break;
        }
        if (keepProgressFrom is not null)
        {
            r.Progress.RoundCount = keepProgressFrom.Progress.RoundCount;
        }
        r.Normalize();
        return r;
    }

    private static void ApplySimple(RuleSettings r)
    {
        r.Revolution.CounterRevolutionEnabled = true;
        r.Sequence.Enabled = false;
        r.Lock.SuitLockEnabled = false;
        r.ElevenBack.Enabled = false;
        r.Joker.SpadeThreeReturnEnabled = false;
        r.RankChange.MiyakoOchiEnabled = false;
        r.ForbiddenFinish.Enabled = false;
    }

    private static void ApplyChaos(RuleSettings r)
    {
        r.Revolution.SequenceRevolutionEnabled = true;
        r.Revolution.CoupEnabled = true;
        r.Revolution.OmenEnabled = true;
        r.Revolution.NanasanEnabled = true;
        r.Revolution.RevolutionIsOptional = true;
        r.Sequence.EmperorEnabled = true;
        r.Sequence.EmperorCausesRevolution = true;
        r.Sequence.EffectsInSequence = true;
        r.Lock.NumberLockEnabled = true;
        r.Lock.StrictLockEnabled = true;
        r.Lock.PartialSuitLockEnabled = true;
        r.Flow.FourStopEnabled = true;
        r.Flow.SandstormEnabled = true;
        r.Flow.AmbulanceEnabled = true;
        r.Flow.RokurokubiEnabled = true;
        r.ElevenBack.StrongElevenBackEnabled = true;
        r.SkipReverse.FiveSkipEnabled = true;
        r.SkipReverse.ThirteenSkipEnabled = true;
        r.SkipReverse.NineReverseEnabled = true;
        r.SkipReverse.TwelveReverseEnabled = true;
        r.Special.SevenPassEnabled = true;
        r.Special.TenDiscardEnabled = true;
        r.Special.TwelveBomberEnabled = true;
        r.Special.DownNumberEnabled = true;
        r.Joker.JokerTriggersEffects = true;
        r.RankChange.GekokujoEnabled = true;
        r.Exchange.TenpenChiiEnabled = true;
        r.ForbiddenFinish.ForbidElevenBack = true;
        r.ForbiddenFinish.ForbidTwelveBomber = true;
        r.ForbiddenFinish.ForbidOtherFlushEffects = true;
        r.ForbiddenFinish.ForbidRevolution = true;
    }
}
