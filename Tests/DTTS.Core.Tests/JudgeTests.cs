using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

internal static class JudgeTests
{
    private static int _fail;
    private static int _pass;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(judge): " + name); }
    }

    private static IReadOnlyList<Card> C(string s) => Card.ParseMany(s);

    private static FieldState Play(JudgeLogic j, FieldState f, string cards, int player = 0)
    {
        var r = j.Judge(C(cards), f);
        if (!r.IsLegal) throw new Exception($"setup play illegal: {cards} {r}");
        return f.WithPlay(player, r.Play!, r.NextLock);
    }

    /// <summary>砂嵐の割り込み・2枚階段・3枚縛り・11バックの解除条件。</summary>
    private static void NewRuleTests()
    {
        var empty = FieldState.CreateInitial();

        // ---- 砂嵐の割り込み（ろくろ首・99車の直後。枚数・縛りを問わず砂嵐だけが出せる）----
        {
            var rules = RuleSettings.CreateDefault();
            rules.Flow.SandstormEnabled = true;
            rules.Flow.RokurokubiEnabled = true;
            rules.Flow.AmbulanceEnabled = true;
            var j = new JudgeLogic(rules);
            var pending = Play(j, empty, "S6 H6").WithSandstormStopPending(true);
            var stop = j.Judge(C("S3 H3 D3"), pending);
            Check(stop.IsLegal && stop.BeatMethod == BeatMethod.SandstormStop && stop.FlushesField
                && stop.HasEffect(SpecialEffectType.Sandstorm), "sandstorm stops a 2-card rokurokubi with 3 cards");
            Check(!stop.NextLock.IsAnyLocked, "sandstorm stop creates no lock");
            Check(j.Judge(C("S7 H7"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "only sandstorm during the window (pair)");
            Check(j.Judge(C("S2"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "only sandstorm during the window (single)");
            Check(j.Judge(C("S3 H3"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "two 3s are not a sandstorm");
            Check(j.Judge(C("S3 H3 D3 C3"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "four 3s are not a sandstorm");
            Check(j.Judge(C("S3 H3 JK0"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "joker does not substitute by default");
            var legal = j.GetLegalPlays(C("S3 H3 D3 C3 S9 H9 JK0"), pending);
            Check(legal.Count == 4 && legal.All(x => x.BeatMethod == BeatMethod.SandstormStop && x.Play!.Count == 3),
                "legal plays during the window are the 3-card sandstorms only: " + legal.Count);
            Check(j.GetLegalPlays(C("S3 H3 S9 H9 D2"), pending).Count == 0, "no sandstorm -> no legal play");
            // スート縛りが掛かっていても割り込める
            var locked = Play(j, Play(j, empty, "S5 H5"), "S6 H6").WithSandstormStopPending(true);
            Check(locked.Lock.IsSuitLocked && j.Judge(C("C3 D3 H3"), locked).BeatMethod == BeatMethod.SandstormStop, "sandstorm stop ignores the suit lock");
            // 受付が終われば（場札が置かれる・流れる）通常の判定に戻る
            Check(!pending.WithPlay(1, stop.Play!, stop.NextLock).IsSandstormStopPending, "window closes when a card is played");
            Check(!pending.Flushed().IsSandstormStopPending, "window closes on flush");
            // 通常時（受付中でない）の砂嵐も、どんな場でも出せる（枚数違い・縛り中でも）
            var normal = Play(j, empty, "S6 H6");
            Check(j.Judge(C("S3 H3 D3"), normal) is { IsLegal: true, BeatMethod: BeatMethod.Sandstorm } any && any.FlushesField,
                "sandstorm can be played over a pair outside the window and flushes");
            Check(j.Judge(C("S3 H3 D3"), locked.WithSandstormStopPending(false)).BeatMethod == BeatMethod.Sandstorm, "sandstorm ignores a suit lock");
            rules.Joker.JokerTriggersEffects = true;
            Check(j.Judge(C("S3 H3 JK0"), pending).BeatMethod == BeatMethod.SandstormStop, "joker substitutes when joker effects are on");
            rules.Flow.SandstormEnabled = false;
            Check(j.Judge(C("S3 H3 D3"), pending).RejectReason == PlayRejectReason.SandstormStopOnly, "sandstorm disabled -> nothing can interrupt");
        }

        // ---- 2枚階段 ----
        {
            var rules = RuleSettings.CreateDefault();
            var j = new JudgeLogic(rules);
            Check(j.Analyze(C("S3 S4")).Count == 0, "2-card run is not a sequence by default");
            Check(!j.GetLegalPlays(C("S3 S4 H9"), empty).Any(x => x.Play!.Type == HandType.Sequence), "no 2-card sequence enumerated by default");
            rules.Sequence.MinLength = 2;
            var two = j.Analyze(C("S3 S4"));
            Check(two.Count == 1 && two[0].Type == HandType.Sequence && two[0].Count == 2, "2-card sequence");
            Check(j.Analyze(C("S3 H4")).Count == 0, "2 cards of different suits are not a sequence");
            Check(j.Analyze(C("S3 S5")).Count == 0, "2 cards with a gap are not a sequence");
            Check(j.Analyze(C("S2 S3")).Count == 0, "no wrap for a 2-card sequence");
            Check(j.Analyze(C("SA S2")).Count == 1, "A-2 is a 2-card sequence");
            // ジョーカー＋1枚：ペア・1つ下からの階段・1つ上への階段の3通り。親出しではペアが優先される
            var withJoker = j.Analyze(C("S5 JK0"));
            Check(withJoker.Count == 3 && withJoker.Count(x => x.Type == HandType.Sequence) == 2, "S5+JK interpretations=3 got " + withJoker.Count);
            Check(j.Judge(C("S5 JK0"), empty).Play!.Type == HandType.Multiple, "S5+JK leads as a pair");
            var fSeq2 = Play(j, empty, "H3 H4");
            Check(fSeq2.CurrentPlay!.Type == HandType.Sequence, "2-card sequence on the field");
            Check(j.Judge(C("S5 S6"), fSeq2).IsLegal, "2-card sequence beats a weaker one");
            Check(j.Judge(C("S4 S5"), fSeq2).RejectReason == PlayRejectReason.NotStronger, "overlapping 2-card sequence is not stronger");
            Check(j.Judge(C("S7 H7"), fSeq2).RejectReason == PlayRejectReason.TypeMismatch, "pair cannot beat a 2-card sequence");
            Check(j.Judge(C("S5 S6 S7"), fSeq2).RejectReason == PlayRejectReason.CountMismatch, "3-card sequence cannot beat a 2-card one");
            var onSeq = j.Judge(C("S5 JK0"), fSeq2);
            Check(onSeq.IsLegal && onSeq.Play!.Type == HandType.Sequence, "S5+JK answers a sequence as a sequence");
            var fPair = Play(j, empty, "H3 D3");
            Check(j.Judge(C("S5 S6"), fPair).RejectReason == PlayRejectReason.TypeMismatch, "2-card sequence cannot beat a pair");
            var enumerated = j.GetLegalPlays(C("S3 S4 S5 H9"), empty).Where(x => x.Play!.Type == HandType.Sequence).ToList();
            Check(enumerated.Count == 3, "2-card sequences enumerated (S3S4, S4S5, S3S4S5): " + enumerated.Count);
            Check(j.GetLegalPlays(C("S5 S6 D9 CK"), fSeq2).Count == 1, "legal answer to a 2-card sequence");
            // 階段革命（既定5枚）は2枚階段では起きない
            rules.Revolution.SequenceRevolutionEnabled = true;
            Check(!j.Judge(C("S3 S4"), empty).TriggersRevolution, "2-card sequence is not a revolution");
        }

        // ---- 3枚縛り（縛り発生回数 2）----
        {
            var rules = RuleSettings.CreateDefault();
            rules.Lock.TriggerCount = 2;
            rules.Lock.NumberLockEnabled = true;
            var j = new JudgeLogic(rules);
            var f1 = Play(j, empty, "S4");
            var f2 = Play(j, f1, "S6");
            Check(!f2.Lock.IsSuitLocked && f2.Lock.SuitMatchStreak == 1, "lock3: 2 in a row -> no lock yet");
            Check(j.Judge(C("H9"), f2).IsLegal, "lock3: other suits still allowed after 2");
            var third = j.Judge(C("S9"), f2);
            Check(third.NextLock.IsSuitLocked && third.HasEffect(SpecialEffectType.SuitLock), "lock3: 3 in a row -> suit lock");
            var f3 = Play(j, f2, "S9");
            Check(j.Judge(C("HK"), f3).RejectReason == PlayRejectReason.SuitLockViolation, "lock3: locked after 3");
            // 数しばも同じ回数：4→5 では成立せず、4→5→6 で成立
            var n2 = Play(j, Play(j, empty, "H4"), "D5");
            Check(!n2.Lock.IsNumberLocked && n2.Lock.NumberMatchStreak == 1, "lock3: number lock needs 3 in a row");
            Check(Play(j, n2, "C6").Lock.IsNumberLocked, "lock3: number lock after 3 in a row");
            rules.Lock.TriggerCount = 1;
            Check(Play(j, Play(j, empty, "S4"), "S6").Lock.IsSuitLocked, "lock2: 2 in a row locks");
        }

        // ---- 11バックの解除条件（FieldState の遷移）----
        {
            var f = empty.WithElevenBackToggled(persistent: false);
            Check(f.IsElevenBack && !f.IsElevenBackPersistent && f.IsStrengthReversed, "11back toggled on");
            var flushed = f.Flushed();
            Check(!flushed.IsElevenBack && !flushed.IsElevenBackPersistent && !flushed.IsStrengthReversed, "normal 11back ends on flush");
            Check(!f.WithElevenBackToggled(false).IsElevenBack, "second J cancels the 11back");
            var strong = empty.WithElevenBackToggled(persistent: true);
            Check(strong.Flushed().IsElevenBack && strong.Flushed().Flushed().IsElevenBackPersistent, "strong 11back survives flushes");
            var released = strong.Flushed().WithElevenBackToggled(true);
            Check(!released.IsElevenBack && !released.IsElevenBackPersistent, "strong 11back released by the next J");
            // 強化で掛かった反転を通常の J で解除しても持続フラグは残らない
            Check(!strong.WithElevenBackToggled(false).Flushed().IsElevenBack, "no stale persistent flag");
            // 革命中の11バックは打ち消し合い、場が流れると革命だけが残る
            var rev = (empty with { IsRevolution = true }).WithElevenBackToggled(false);
            Check(!rev.IsStrengthReversed && rev.Flushed().IsStrengthReversed && rev.Flushed().IsRevolution, "11back inside a revolution");
            var fresh = FieldState.CreateInitial();
            Check(!fresh.IsElevenBack && !fresh.IsElevenBackPersistent && !fresh.IsRevolution && !fresh.IsSandstormStopPending, "new round field is clean");
            // 「次の手番だけ」：フラグは反転中だけ立ち、場流れ・解除・次のJで落ちる
            var one = empty.WithElevenBackToggled(persistent: false, oneTurn: true);
            Check(one.IsElevenBack && one.IsElevenBackOneTurn && !one.IsElevenBackPersistent && one.IsStrengthReversed, "one-turn 11back on");
            Check(!one.Flushed().IsElevenBack && !one.Flushed().IsElevenBackOneTurn, "one-turn 11back ends on flush");
            Check(!one.WithElevenBackCleared().IsElevenBack && !one.WithElevenBackCleared().IsElevenBackOneTurn, "one-turn 11back cleared");
            Check(!one.WithElevenBackToggled(false, oneTurn: true).IsElevenBackOneTurn, "toggling off drops the one-turn flag");
            Check(!empty.WithElevenBackToggled(persistent: true, oneTurn: true).IsElevenBackOneTurn, "persistent wins over one-turn");

            // 判定側：強化の有無で効果の種類が変わる
            var rules = RuleSettings.CreateDefault();
            var j = new JudgeLogic(rules);
            Check(j.Judge(C("SJ"), empty).HasEffect(SpecialEffectType.ElevenBack)
                && !j.Judge(C("SJ"), empty).HasEffect(SpecialEffectType.StrongElevenBack), "normal 11back effect");
            Check(j.Judge(C("SJ HJ DJ"), empty).HasEffect(SpecialEffectType.StrongElevenBack), "strong 11back effect on 3 jacks");
            rules.ElevenBack.Enabled = false;
            Check(!j.Judge(C("SJ HJ DJ"), empty).HasEffect(SpecialEffectType.StrongElevenBack)
                && !j.Judge(C("SJ"), empty).HasEffect(SpecialEffectType.ElevenBack), "11back disabled");
        }
    }

    public static (int pass, int fail) Run()
    {
        _pass = 0;
        _fail = 0;
        var rules = RuleSettings.CreateDefault();
        var j = new JudgeLogic(rules);
        var empty = FieldState.CreateInitial();

        // 強さ
        Check(Card.Parse("S3").BaseStrength == 1, "3 base 1");
        Check(Card.Parse("H2").BaseStrength == 13, "2 base 13");
        Check(Card.Parse("CA").BaseStrength == 12, "A base 12");
        Check(Card.Parse("S3").GetStrength(true) == 13, "3 reversed 13");
        Check(Card.Joker(1).GetStrength(true) == 14, "joker reversed 14");
        for (int b = 1; b <= 13; b++) Check(Card.GetBaseStrength(Card.GetRankFromBaseStrength(b)) == b, "roundtrip " + b);
        Check(Card.FromId(Card.Parse("DQ").Id) == Card.Parse("DQ"), "fromId");
        Check(!(empty with { IsRevolution = true, IsElevenBack = true }).IsStrengthReversed, "xor");
        Check(Card.CreateDeck(2).Count == 54 && Card.CreateDeck(2).Distinct().Count() == 54, "deck");

        // 解析
        Check(j.Analyze(C("H5 JK0")).Single().Type == HandType.Multiple, "pair w/ joker");
        var seq = j.Analyze(C("S3 S5 JK0"));
        Check(seq.Count == 1 && seq[0].Type == HandType.Sequence && seq[0].Rank == Rank.Three, "seq gap fill");
        var multi = j.Analyze(C("S5 JK0 JK1"));
        Check(multi.Count == 4, "S5+2JK interpretations=4 got " + multi.Count);
        Check(j.Analyze(C("S3 H4 D5")).Count == 0, "mixed suit not seq");
        Check(j.Analyze(C("SK SA S2")).Count == 1, "K-A-2 ok");
        Check(j.Analyze(C("SA S2 S3")).Count == 0, "no wrap");
        Check(j.Analyze(new[] { Card.Parse("S3"), Card.Parse("S3") }).Count == 0, "dup invalid");

        // 階段比較
        var fSeq = Play(j, empty, "S3 S4 S5");
        Check(j.Judge(C("H6 H7 H8"), fSeq).IsLegal, "seq stronger");
        Check(j.Judge(C("H5 H6 H7"), fSeq).RejectReason == PlayRejectReason.NotStronger, "seq overlap not stronger");
        Check(j.Judge(C("H6 H7"), fSeq).RejectReason != PlayRejectReason.None, "seq count");
        Check(j.Judge(C("H6 JK0 H8"), fSeq).IsLegal, "seq joker gap");

        // 単騎・ジョーカー
        var f2 = Play(j, empty, "H2");
        Check(j.Judge(C("JK0"), f2).IsLegal, "joker beats 2");
        var fJ = Play(j, empty, "JK0");
        Check(!j.Judge(C("H2"), fJ).IsLegal, "2 not beat joker");
        Check(!j.Judge(C("JK1"), fJ).IsLegal, "joker not beat joker");
        var s3 = j.Judge(C("S3"), fJ);
        Check(s3.IsLegal && s3.BeatMethod == BeatMethod.SpadeThreeReturn && s3.FlushesField, "spade3 return");
        Check(!j.Judge(C("H3"), fJ).IsLegal, "heart3 no return");
        var fJpair = Play(j, empty, "JK0 JK1");
        Check(!j.Judge(C("S3"), fJpair).IsLegal, "spade3 vs joker pair");
        rules.Joker.SpadeThreeReturnEnabled = false;
        Check(!j.Judge(C("S3"), fJ).IsLegal, "spade3 disabled");
        rules.Joker.SpadeThreeReturnEnabled = true;

        // 反転
        var fRev = Play(j, empty with { IsRevolution = true }, "S4");
        Check(j.Judge(C("H3"), fRev).IsLegal, "rev 3 beats 4");
        Check(!j.Judge(C("H2"), fRev).IsLegal, "rev 2 weak");
        var fEb = Play(j, empty with { IsRevolution = true, IsElevenBack = true }, "S4");
        Check(j.Judge(C("H5"), fEb).IsLegal && !j.Judge(C("H3"), fEb).IsLegal, "rev xor 11back normal");
        var fRevSeq = Play(j, empty with { IsRevolution = true }, "H9 H10 HJ");
        Check(j.Judge(C("S6 S7 S8"), fRevSeq).IsLegal && !j.Judge(C("S8 S9 S10"), fRevSeq).IsLegal, "rev seq");

        // スート縛り
        var fL = Play(j, Play(j, empty, "S4"), "S5");
        Check(fL.Lock.IsSuitLocked && fL.Lock.LockedSuits.SequenceEqual(new[] { Suit.Spade }), "spade locked");
        Check(j.Judge(C("H9"), fL).RejectReason == PlayRejectReason.SuitLockViolation, "lock violation");
        Check(j.Judge(C("S9"), fL).IsLegal, "lock ok");
        Check(j.Judge(C("JK0"), fL).IsLegal, "joker in lock");
        var fNoLock = Play(j, Play(j, empty, "S4"), "JK0");
        Check(!fNoLock.Lock.IsSuitLocked, "joker alone no lock");
        var fPL = Play(j, Play(j, empty, "S4 H4"), "S6 JK0");
        Check(fPL.Lock.IsSuitLocked && fPL.Lock.LockedSuits.SequenceEqual(new[] { Suit.Spade, Suit.Heart }), "pair lock w joker: " + fPL.Lock);
        Check(j.Judge(C("S9 H9"), fPL).IsLegal && !j.Judge(C("S9 D9"), fPL).IsLegal, "pair lock enforcement");
        rules.Lock.SuitLockEnabled = false;
        Check(!Play(j, Play(j, empty, "S4"), "S5").Lock.IsSuitLocked, "suit lock disabled");
        rules.Lock.SuitLockEnabled = true;

        // 縛り発生回数2
        rules.Lock.TriggerCount = 2;
        var fL2 = Play(j, Play(j, empty, "S4"), "S5");
        Check(!fL2.Lock.IsSuitLocked, "trigger2 not yet");
        var fL3 = Play(j, fL2, "S6");
        Check(fL3.Lock.IsSuitLocked, "trigger2 locked");
        rules.Lock.TriggerCount = 1;

        // 数しば
        rules.Lock.NumberLockEnabled = true;
        var fN = Play(j, Play(j, empty, "S4"), "H5");
        Check(fN.Lock.IsNumberLocked, "number lock");
        Check(j.Judge(C("D7"), fN).RejectReason == PlayRejectReason.NumberLockViolation, "number lock violation");
        Check(j.Judge(C("D6"), fN).BeatMethod == BeatMethod.NumberLockFollow, "number lock follow");
        var fBoth = Play(j, Play(j, empty, "S4"), "S5");
        Check(fBoth.Lock.IsSuitLocked && !fBoth.Lock.IsNumberLocked, "no strict -> suit only");
        rules.Lock.StrictLockEnabled = true;
        var fBoth2 = Play(j, Play(j, empty, "S4"), "S5");
        Check(fBoth2.Lock.IsSuitLocked && fBoth2.Lock.IsNumberLocked, "strict both");
        rules.Lock.StrictLockEnabled = false;
        rules.Lock.NumberLockEnabled = false;

        // 両しば
        rules.Lock.PartialSuitLockEnabled = true;
        var fP = Play(j, Play(j, empty, "S4 H4"), "S6 D6");
        Check(fP.Lock.IsSuitLocked && fP.Lock.LockedSuits.SequenceEqual(new[] { Suit.Spade, Suit.Joker }), "partial lock " + fP.Lock);
        Check(j.Judge(C("S9 C9"), fP).IsLegal && !j.Judge(C("H9 C9"), fP).IsLegal, "partial lock enforce");
        rules.Lock.PartialSuitLockEnabled = false;

        // 効果
        Check(j.Judge(C("H8"), empty).HasEffect(SpecialEffectType.EightCut), "8 cut");
        Check(!j.Judge(C("H6 H7 H8"), empty).HasEffect(SpecialEffectType.EightCut), "8 in seq no effect");
        rules.Sequence.EffectsInSequence = true;
        Check(j.Judge(C("H6 H7 H8"), empty).HasEffect(SpecialEffectType.EightCut), "8 in seq effect");
        rules.Sequence.EffectsInSequence = false;
        Check(!j.Judge(C("H7 JK0"), empty).HasEffect(SpecialEffectType.SevenPass), "7pass disabled default");
        rules.Joker.JokerTriggersEffects = true;
        rules.Sequence.EffectsInSequence = true;
        Check(j.Judge(C("H6 H7 JK0"), empty).Alternatives.Any(), "joker seq");
        rules.Joker.JokerTriggersEffects = false;
        rules.Sequence.EffectsInSequence = false;
        Check(j.Judge(C("HJ"), empty).HasEffect(SpecialEffectType.ElevenBack), "11back");
        Check(j.Judge(C("HJ SJ DJ"), empty).HasEffect(SpecialEffectType.StrongElevenBack), "strong 11back on 3 jacks");
        rules.Flow.EightCutEnabled = false;
        Check(!j.Judge(C("H8"), empty).HasEffect(SpecialEffectType.EightCut), "8 cut disabled");
        rules.Flow.EightCutEnabled = true;
        rules.Flow.EightCutRank = Rank.Nine;
        Check(j.Judge(C("H9"), empty).HasEffect(SpecialEffectType.EightCut) && !j.Judge(C("H8"), empty).HasEffect(SpecialEffectType.EightCut), "8 cut custom rank");
        rules.Flow.EightCutRank = Rank.Eight;
        rules.SkipReverse.FiveSkipEnabled = true;
        Check(j.Judge(C("H5 S5"), empty).GetEffect(SpecialEffectType.FiveSkip)?.Magnitude == 2, "5skip x2");
        rules.SkipReverse.FiveSkipEnabled = false;

        // 革命
        Check(j.Judge(C("S6 H6 D6 C6"), empty).HasEffect(SpecialEffectType.Revolution), "revolution 4");
        Check(j.Judge(C("S6 H6 D6 JK0"), empty).TriggersRevolution, "revolution w joker");
        rules.Revolution.JokerCountsForRevolution = false;
        Check(!j.Judge(C("S6 H6 D6 JK0"), empty).TriggersRevolution, "joker not counted");
        rules.Revolution.JokerCountsForRevolution = true;
        rules.Revolution.CounterRevolutionEnabled = false;
        Check(!j.Judge(C("S6 H6 D6 C6"), empty with { IsRevolution = true }).TriggersRevolution, "no counter rev");
        rules.Revolution.CounterRevolutionEnabled = true;
        Check(j.Judge(C("S6 H6 D6 C6"), empty with { IsRevolution = true }).TriggersRevolution, "counter rev");
        rules.Revolution.CoupEnabled = true;
        Check(j.Judge(C("S9 H9 D9"), empty).HasEffect(SpecialEffectType.Coup), "coup");
        rules.Revolution.CoupEnabled = false;
        rules.Revolution.OmenEnabled = true;
        Check(j.Judge(C("S6 H6 D6"), empty).HasEffect(SpecialEffectType.Omen), "omen");
        rules.Revolution.OmenEnabled = false;
        Check(!j.Judge(C("S9 H9 D9 C9"), empty with { IsRevolutionLocked = true }).TriggersRevolution, "omen lock");
        rules.Revolution.RevolutionIsOptional = true;
        Check(j.Judge(C("S6 H6 D6 C6"), empty).GetEffect(SpecialEffectType.Revolution)!.IsOptional, "optional rev");
        rules.Revolution.RevolutionIsOptional = false;
        rules.Revolution.SequenceRevolutionEnabled = true;
        Check(j.Judge(C("S3 S4 S5 S6 S7"), empty).HasEffect(SpecialEffectType.SequenceRevolution), "seq revolution");
        rules.Revolution.SequenceRevolutionEnabled = false;

        // 砂嵐・99車
        rules.Flow.AmbulanceEnabled = true;
        var fPair2 = Play(j, empty, "S2 H2");
        // 99車は、ろくろ首と同じく通常どおり場に勝てるときだけ出せる（出せれば場が流れる）
        Check(j.Judge(C("D9 C9"), fPair2).RejectReason == PlayRejectReason.NotStronger, "ambulance cannot beat a pair of 2s");
        var fPair5 = Play(j, empty, "S5 H5");
        var amb = j.Judge(C("D9 C9"), fPair5);
        Check(amb.IsLegal && amb.BeatMethod == BeatMethod.Stronger && amb.HasEffect(SpecialEffectType.Ambulance) && amb.FlushesField,
            "ambulance beats a weaker pair and flushes");
        Check(j.Judge(C("D9 C9"), empty) is { IsLegal: true } lead && lead.HasEffect(SpecialEffectType.Ambulance), "ambulance as a lead flushes");
        var revPair4 = Play(j, empty, "S4 H4").WithRevolutionToggled();
        Check(j.Judge(C("D9 C9"), revPair4).RejectReason == PlayRejectReason.NotStronger, "ambulance: 9s lose to 4s during a revolution");
        rules.Joker.JokerCount = 1;
        rules.Joker.JokerTriggersEffects = true;
        Check(!j.Judge(C("D9 C9 JK0"), Play(j, empty, "S4 H4 D4").WithRevolutionToggled()).IsLegal, "ambulance-like 9x2+JK cannot beat 4s in a revolution");
        rules.Joker.JokerTriggersEffects = false;
        rules.Flow.AmbulanceEnabled = false;
        Check(j.Judge(C("D9 C9"), fPair5) is { IsLegal: true } plain && !plain.HasEffect(SpecialEffectType.Ambulance), "ambulance disabled: just a pair");

        // 砂嵐：どんな場でも（1枚・ペア・階段・縛り・革命中）出せて、すぐに場が流れる
        rules.Flow.SandstormEnabled = true;
        Check(j.Judge(C("S3 H3 D3"), Play(j, empty, "S2 H2 D2")).BeatMethod == BeatMethod.Sandstorm, "sandstorm over a triple");
        Check(j.Judge(C("S3 H3 D3"), Play(j, empty, "S2")) is { IsLegal: true, BeatMethod: BeatMethod.Sandstorm } s1 && s1.FlushesField, "sandstorm over a single");
        Check(j.Judge(C("S3 H3 D3"), Play(j, empty, "H9 H10 HJ")).BeatMethod == BeatMethod.Sandstorm, "sandstorm over a sequence");
        Check(j.Judge(C("S3 H3 D3"), Play(j, empty, "S2 H2").WithRevolutionToggled()).BeatMethod == BeatMethod.Sandstorm, "sandstorm during a revolution");
        var handWithStorm = C("S3 H3 D3 C9");
        var legalOverSingle = j.GetLegalPlays(handWithStorm, Play(j, empty, "S2"));
        Check(legalOverSingle.Any(p => p.BeatMethod == BeatMethod.Sandstorm), "sandstorm is listed as a legal play over a single");
        Check(j.Judge(C("S3 H3"), Play(j, empty, "S2")).IsLegal == false, "two 3s are not a sandstorm");

        // 4止めの成立後：場が流れるまで砂嵐しか出せない
        var fourLocked = Play(j, empty, "S4 H4").WithFourStopLocked(true);
        Check(j.Judge(C("S5 H5"), fourLocked).RejectReason == PlayRejectReason.FourStopLocked, "after a four stop a stronger pair is refused");
        Check(j.Judge(C("S3 H3 D3"), fourLocked).BeatMethod == BeatMethod.Sandstorm, "after a four stop the sandstorm is allowed");
        var lockedLegal = j.GetLegalPlays(C("S5 H5 S3 H3 D3 C2"), fourLocked);
        Check(lockedLegal.Count == 1 && lockedLegal[0].BeatMethod == BeatMethod.Sandstorm, "after a four stop only the sandstorm is legal");
        Check(!fourLocked.Flushed().IsFourStopLocked, "four stop lock ends on flush");
        rules.Flow.SandstormEnabled = false;
        Check(j.GetLegalPlays(C("S5 H5 S3 H3 D3"), fourLocked).Count == 0, "after a four stop without sandstorm rule: nothing is legal");

        // 4止め
        rules.Flow.FourStopEnabled = true;
        var f8 = Play(j, empty, "H8").WithEightCutPending(true);
        // 標準：8の枚数の2倍の4が必要（8が1枚なら4を2枚）
        Check(j.Judge(C("S4 H4"), f8).BeatMethod == BeatMethod.FourStop, "four stop: 4-pair stops a single 8");
        Check(j.Judge(C("S4"), f8).RejectReason == PlayRejectReason.FourStopOnly, "four stop: one 4 is not enough by default");
        Check(j.Judge(C("S4 H4 C4"), f8).RejectReason == PlayRejectReason.FourStopOnly, "four stop: three 4s are not the required count");
        Check(j.Judge(C("S9"), f8).RejectReason == PlayRejectReason.FourStopOnly, "four stop only");
        var f88 = Play(j, empty, "H8 D8").WithEightCutPending(true);
        Check(j.Judge(C("S4 H4 C4 D4"), f88).BeatMethod == BeatMethod.FourStop, "four stop: four 4s stop two 8s");
        Check(!j.Judge(C("S4 H4"), f88).IsLegal, "four stop: two 4s cannot stop two 8s by default");
        Check(j.GetLegalPlays(C("S4 H4 C5"), f8).Count == 1 && j.GetLegalPlays(C("S4 C5"), f8).Count == 0,
            "four stop: legal plays use the doubled count");
        Check(j.Judge(C("S4 JK0"), f8).RejectReason == PlayRejectReason.FourStopOnly, "four stop: joker substitute needs joker effects");
        rules.Joker.JokerTriggersEffects = true;
        Check(j.Judge(C("S4 JK0"), f8).BeatMethod == BeatMethod.FourStop, "four stop: joker substitute with joker effects");
        rules.Joker.JokerTriggersEffects = false;
        // 階段の8切りは止められない
        var fSeq8 = Play(j, empty, "H7 H8 H9").WithEightCutPending(true);
        Check(!j.Judge(C("S4 H4 C4 D4 JK0 C5"), fSeq8).IsLegal && j.GetLegalPlays(C("S4 H4 C4 D4"), fSeq8).Count == 0,
            "four stop: a sequence 8-cut cannot be stopped");
        rules.Flow.FourStopEnabled = false;

        // エンペラー
        rules.Sequence.EmperorEnabled = true;
        var emp = j.Judge(C("S4 H5 D6 C7"), empty);
        Check(emp.IsLegal && emp.Play!.Type == HandType.Emperor && emp.HasEffect(SpecialEffectType.Emperor), "emperor");
        Check(j.Judge(C("S4 H5 D6 C7"), Play(j, empty, "SJ SQ SK SA")).BeatMethod == BeatMethod.Emperor, "emperor beats seq");
        Check(j.GetLegalPlays(C("S4 H5 D6 C7"), empty).Any(x => x.Play!.Type == HandType.Emperor), "enum emperor");
        rules.Sequence.EmperorEnabled = false;
        Check(j.Analyze(C("S4 H5 D6 C7")).Count == 0, "emperor disabled");

        // ダウンナンバー
        rules.Special.DownNumberEnabled = true;
        var fD = Play(j, empty, "H9");
        Check(j.Judge(C("H8"), fD).BeatMethod == BeatMethod.DownNumber, "down number");
        Check(!j.Judge(C("S8"), fD).IsLegal, "down number suit");
        rules.Special.DownNumberEnabled = false;

        // 禁止あがり
        Check(j.Judge(C("H2"), empty, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "forbid 2");
        Check(!j.Judge(C("H2"), empty with { IsRevolution = true }, true).IsForbiddenFinish, "rev 2 ok");
        Check(j.Judge(C("H3"), empty with { IsRevolution = true }, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "rev forbid 3");
        // 11バック中（通常11バック／革命＋11バック）は 3 も 2 も禁止あがりにならない
        Check(!j.Judge(C("H3"), empty with { IsElevenBack = true }, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "11back 3 ok");
        Check(!j.Judge(C("H2"), empty with { IsElevenBack = true }, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "11back 2 ok");
        Check(!j.Judge(C("H2"), empty with { IsRevolution = true, IsElevenBack = true }, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "rev+11back 2 ok");
        Check(!j.Judge(C("H3"), empty with { IsRevolution = true, IsElevenBack = true }, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.StrongestNumber), "rev+11back 3 ok");
        rules.ForbiddenFinish.StrongestFollowsReversal = false;
        Check(!j.Judge(C("H3"), empty with { IsRevolution = true }, true).IsForbiddenFinish, "fixed 2 forbid");
        rules.ForbiddenFinish.StrongestFollowsReversal = true;
        Check(j.Judge(C("H8"), empty, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.EightCut), "forbid 8");
        Check(j.Judge(C("JK0"), empty, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.Joker), "forbid joker");
        Check(j.Judge(C("S3"), fJ, true).ForbiddenFinish.HasFlag(ForbiddenFinishReason.SpadeThree), "forbid spade3");
        Check(!j.Judge(C("H6 H7 H9 H10".Replace("H9 ", "H8 H9 ").Split(' ').Take(3).Aggregate((a, b) => a + " " + b)), empty, true).IsForbiddenFinish, "8 in seq finish ok");
        Check(!j.Judge(C("H8"), empty, false).IsForbiddenFinish, "not finishing");
        rules.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.RejectPlay;
        Check(j.Judge(C("H2"), empty, true).RejectReason == PlayRejectReason.ForbiddenFinish, "reject forbidden");
        rules.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.LastPlace;
        rules.ForbiddenFinish.Enabled = false;
        Check(!j.Judge(C("H2"), empty, true).IsForbiddenFinish, "forbidden disabled");
        rules.ForbiddenFinish.Enabled = true;

        // ダイヤ3初手
        var fD3 = FieldState.CreateInitial(requiredCard: Card.Parse("D3"));
        Check(j.Judge(C("S4"), fD3).RejectReason == PlayRejectReason.MissingRequiredCard, "d3 required");
        Check(j.Judge(C("D3 S3"), fD3).IsLegal, "d3 pair");
        Check(Play(j, fD3, "D3").RequiredCard is null, "d3 cleared");

        // 合法手列挙
        var hand = C("S3 H3 S4 S5 D9 JK0");
        var legalEmpty = j.GetLegalPlays(hand, empty);
        Check(legalEmpty.Any(x => x.Play!.Type == HandType.Sequence), "enum seq");
        Check(legalEmpty.Any(x => x.Play!.Count == 3 && x.Play.Type == HandType.Multiple), "enum triple w joker");
        var legalOnPair = j.GetLegalPlays(hand, Play(j, empty, "D4 C4"));
        Check(legalOnPair.All(x => x.Play!.Count == 2) && legalOnPair.Count > 0, "enum on pair");
        Check(j.GetLegalPlays(C("S3 H4"), fJ).Count == 1, "only spade3 on joker");
        var bigHand = Card.CreateDeck(2).Take(14).ToList();
        Check(j.GetLegalPlays(bigHand, empty).Count > 0, "big hand enum");

        // 場流し
        var flushed = fL.WithElevenBackToggled(false).Flushed();
        Check(flushed.IsEmpty && !flushed.Lock.IsAnyLocked && !flushed.IsElevenBack && flushed.TrickNumber == 1, "flush");
        Check(fL.WithElevenBackToggled(true).Flushed().IsElevenBack, "strong 11back persists");
        Check(fL.WithRevolutionToggled().Flushed().IsRevolution, "revolution persists flush");
        Check(fL.WithPass(2).HasPassed(2) && !fL.WithPass(2).Flushed().HasPassed(2), "pass history");

        // JSON
        rules.Flow.SandstormEnabled = true;
        rules.Joker.JokerCount = 2;
        string json = rules.ToJson();
        var back = RuleSettings.FromJson(json);
        Check(back.Flow.SandstormEnabled && back.Joker.JokerCount == 2 && back.Flow.EightCutRank == Rank.Eight, "json roundtrip");
        Check(json.Contains("Eight", StringComparison.Ordinal), "string enum");
        var partial = RuleSettings.FromJson("{\"joker\":{\"jokerCount\":9}}");
        Check(partial.Joker.JokerCount == 2 && partial.Flow.EightCutEnabled, "normalize + defaults");
        back.ResetToDefault();
        Check(!back.Flow.SandstormEnabled && back.Joker.JokerCount == 1, "reset");
        Check(rules.Clone().Flow.SandstormEnabled, "clone");

        // ---- 追加チェック ----
        // 99車は数しばに従う
        rules.Flow.AmbulanceEnabled = true;
        rules.Lock.NumberLockEnabled = true;
        rules.Lock.SuitLockEnabled = false;
        var fNum = Play(j, Play(j, empty, "S4 H4"), "D5 C5");
        Check(fNum.Lock.IsNumberLocked, "pair number lock");
        Check(j.Judge(C("D9 C9"), fNum).RejectReason == PlayRejectReason.NumberLockViolation, "ambulance obeys number lock");
        Check(j.GetLegalPlays(C("S6 H6 D7 S9 H9"), fNum).All(x => x.Play!.Rank == Rank.Six), "enum under number lock");
        rules.Lock.NumberLockEnabled = false;
        rules.Lock.SuitLockEnabled = true;
        rules.Flow.AmbulanceEnabled = false;
        // 縛り中の合法手列挙
        Check(j.GetLegalPlays(C("S9 H10 DJ JK0"), fL).All(x => x.Play!.NaturalCards.All(c => c.Suit == Suit.Spade)), "enum under suit lock");
        // 階段ゲート
        rules.Sequence.Enabled = false;
        Check(j.Analyze(C("S3 S4 S5")).Count == 0, "seq disabled");
        rules.Sequence.Enabled = true;
        rules.Sequence.AllowTwoInSequence = false;
        Check(j.Analyze(C("SK SA S2")).Count == 0, "no two in seq");
        rules.Sequence.AllowTwoInSequence = true;
        rules.Sequence.AllowJokerInSequence = false;
        Check(j.Analyze(C("S3 S5 JK0")).Count == 0, "no joker in seq");
        rules.Sequence.AllowJokerInSequence = true;
        rules.Sequence.MinLength = 4;
        Check(j.Analyze(C("S3 S4 S5")).Count == 0 && j.Analyze(C("S3 S4 S5 S6")).Count == 1, "seq min length 4");
        rules.Sequence.MinLength = 3;
        // ナナサン・ろくろ首・手札操作・リバース
        rules.Revolution.NanasanEnabled = true;
        Check(j.Judge(C("S7 H7 D7"), empty).HasEffect(SpecialEffectType.Nanasan), "nanasan");
        rules.Revolution.NanasanEnabled = false;
        rules.Flow.RokurokubiEnabled = true;
        Check(j.Judge(C("S6 H6"), empty).HasEffect(SpecialEffectType.Rokurokubi), "rokurokubi");
        Check(!j.Judge(C("S6 H6"), Play(j, empty, "S9 H9")).IsLegal, "rokurokubi needs strength");
        rules.Flow.RokurokubiEnabled = false;
        rules.Special.SevenPassEnabled = true;
        rules.Special.TenDiscardEnabled = true;
        rules.Special.TwelveBomberEnabled = true;
        rules.SkipReverse.NineReverseEnabled = true;
        rules.SkipReverse.TwelveReverseEnabled = true;
        Check(j.Judge(C("S7 H7"), empty).GetEffect(SpecialEffectType.SevenPass)?.Magnitude == 2, "7pass x2");
        Check(j.Judge(C("S10"), empty).GetEffect(SpecialEffectType.TenDiscard)?.Magnitude == 1, "10discard");
        var q3 = j.Judge(C("SQ HQ DQ"), empty);
        Check(q3.GetEffect(SpecialEffectType.TwelveBomber)?.Magnitude == 3 && q3.HasEffect(SpecialEffectType.TwelveReverse), "12 bomber + reverse");
        Check(j.Judge(C("S9"), empty).HasEffect(SpecialEffectType.NineReverse), "9 reverse");
        rules.Special.SevenPassEnabled = false;
        rules.Special.TenDiscardEnabled = false;
        rules.Special.TwelveBomberEnabled = false;
        rules.SkipReverse.NineReverseEnabled = false;
        rules.SkipReverse.TwelveReverseEnabled = false;
        // ジョーカー代用による効果発動
        rules.Joker.JokerTriggersEffects = true;
        Check(j.Judge(C("H8 JK0"), empty).GetEffect(SpecialEffectType.EightCut)?.Magnitude == 2, "joker substitute effect");
        rules.Joker.JokerTriggersEffects = false;
        Check(j.Judge(C("H8 JK0"), empty).GetEffect(SpecialEffectType.EightCut)?.Magnitude == 1, "joker no substitute effect");

        NewRuleTests();

        // 禁止あがり「最下位 (LastPlace)」設定時、ジョーカー入り階段に正常あがり解釈があれば反則にしない
        {
            var r = RuleSettings.CreateDefault();
            r.Sequence.Enabled = true;
            r.Sequence.AllowJokerInSequence = true;
            r.Sequence.EffectsInSequence = true;
            r.Joker.EffectMode = JokerEffectMode.All;
            r.Flow.EightCutEnabled = true;
            r.ForbiddenFinish.Enabled = true;
            r.ForbiddenFinish.ForbidEightCut = true;
            r.ForbiddenFinish.JokerFinishRule = GentleStrictRule.Off;
            r.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.LastPlace;
            var jLast = new JudgeLogic(r);
            var res = jLast.Judge(C("S6 S7 JK0"), FieldState.CreateInitial(), isFinishingPlay: true);
            Check(res.IsLegal && !res.IsForbiddenFinish && res.Play!.Rank == Rank.Five,
                "forbidden finish LastPlace prefers non-forbidden 5-6-7 over 6-7-8");
        }

        Console.WriteLine($"judge: pass={_pass} fail={_fail}");
        return (_pass, _fail);
    }
}
