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
        rules.ElevenBack.StrongElevenBackEnabled = true;
        Check(j.Judge(C("HJ"), empty).HasEffect(SpecialEffectType.StrongElevenBack), "strong 11back");
        rules.ElevenBack.StrongElevenBackEnabled = false;
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
        var amb = j.Judge(C("D9 C9"), fPair2);
        Check(amb.IsLegal && amb.BeatMethod == BeatMethod.Ambulance && amb.FlushesField, "ambulance");
        rules.Flow.AmbulanceEnabled = false;
        Check(!j.Judge(C("D9 C9"), fPair2).IsLegal, "ambulance disabled");
        rules.Flow.SandstormEnabled = true;
        Check(j.Judge(C("S3 H3 D3"), Play(j, empty, "S2 H2 D2")).BeatMethod == BeatMethod.Sandstorm, "sandstorm");
        rules.Flow.SandstormEnabled = false;

        // 4止め
        rules.Flow.FourStopEnabled = true;
        var f8 = Play(j, empty, "H8").WithEightCutPending(true);
        Check(j.Judge(C("S4"), f8).BeatMethod == BeatMethod.FourStop, "four stop");
        Check(j.Judge(C("S9"), f8).RejectReason == PlayRejectReason.FourStopOnly, "four stop only");
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

        Console.WriteLine($"judge: pass={_pass} fail={_fail}");
        return (_pass, _fail);
    }
}
