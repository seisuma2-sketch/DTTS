using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Settings;

/// <summary>
/// 場流しの割り込みの統一（8切り・ろくろ首・99車・エンペラーは確認を経て流れ、砂嵐だけは即座に流れる）、
/// エンペラーの場流し設定、操作ガイドのレベル（止められる人だけに確認／初心者用は常に確認・ヒントの量）のテスト。
/// </summary>
internal static class InterruptTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(interrupt): " + name); }
    }

    private static IReadOnlyList<Card> C(string s) => Card.ParseMany(s);

    private static void Play(GameEngine e, int pid, string cards, string label)
    {
        var r = e.SubmitPlay(pid, C(cards));
        Check(r.IsAccepted, $"{label}: P{pid} play {cards} -> {r}");
    }

    private static void Pass(GameEngine e, int pid, string label)
    {
        var r = e.SubmitPass(pid);
        Check(r.IsAccepted, $"{label}: P{pid} pass -> {r}");
    }

    public static (int Pass, int Fail) Run()
    {
        EmperorFlushSetting();
        EmperorStoppedBySandstorm();
        EightCutStoppedBySandstorm();
        SandstormIsNeverInterrupted();
        JudgeAcceptsBothStoppers();
        GuideHintLevels();
        return (_pass, _fail);
    }

    /// <summary>エンジンを作って開始し、親（l）と進行方向順の3人を返す。席替え・交換なし。</summary>
    private static (GameEngine E, int L, int A, int B, int C, Recorder Rec) Start(RuleSettings r, int seed = 11)
    {
        r.Exchange.Enabled = false;
        r.Lock.SuitLockEnabled = false;
        var e = new GameEngine(r, Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Human)).ToList(), new SystemRandomSource(seed));
        var rec = new Recorder(e);
        e.StartGame();
        int l = e.PendingDecision!.PlayerId;
        return (e, l, (l + 1) % 4, (l + 2) % 4, (l + 3) % 4, rec);
    }

    /// <summary>割り込みの確認・発動した効果・場流しを記録する。</summary>
    private sealed class Recorder
    {
        public List<int> Asked { get; } = new();
        public List<DecisionKind> AskedKinds { get; } = new();
        public List<SpecialEffectType> Effects { get; } = new();
        public List<FieldFlushedEvent> Flushes { get; } = new();

        public Recorder(GameEngine e)
        {
            e.EventOccurred += (_, ev) =>
            {
                switch (ev)
                {
                    case DecisionRequestedEvent dr when dr.Decision.Kind.IsInterruptChance():
                        Asked.Add(dr.Decision.PlayerId);
                        AskedKinds.Add(dr.Decision.Kind);
                        break;
                    case EffectAppliedEvent ea when ea.Applied:
                        Effects.Add(ea.Effect.Type);
                        break;
                    case FieldFlushedEvent ff:
                        Flushes.Add(ff);
                        break;
                }
            };
        }

        public void Clear()
        {
            Asked.Clear();
            AskedKinds.Clear();
            Effects.Clear();
            Flushes.Clear();
        }
    }

    private static RuleSettings EmperorRules(bool flushes, bool sandstorm)
    {
        var r = RuleSettings.CreateDefault();
        r.Sequence.EmperorEnabled = true;
        r.Sequence.EmperorFlushesField = flushes;
        r.Flow.SandstormEnabled = sandstorm;
        return r;
    }

    // ------------------------------------------------------------------
    // エンペラーで場を流すか
    // ------------------------------------------------------------------

    private static void EmperorFlushSetting()
    {
        Check(!RuleSettings.CreateDefault().Sequence.EmperorFlushesField, "emperor: does not flush by default");
        Check(RulePresets.Create(RulePreset.Expert).Sequence.EmperorFlushesField, "emperor: the expert preset flushes");
        {
            var back = RuleSettings.FromJson(EmperorRules(true, false).ToJson());
            Check(back.Sequence.EmperorFlushesField, "emperor: the flush setting survives JSON");
        }

        // (1) 流さない（標準）：エンペラーを出しても場は続き、全員がパスしてから普通に流れる
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: false, sandstorm: true));
            e.GetPlayer(l).SetHand(C("D3 S4 H5 D6 C7 SK"));
            e.GetPlayer(a).SetHand(C("C9 D10"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D9"));   // 砂嵐を持っていても、流れないエンペラーには割り込まない
            e.GetPlayer(c).SetHand(C("DJ CQ"));
            Play(e, l, "D3", "emperor-off");
            Pass(e, a, "emperor-off"); Pass(e, b, "emperor-off"); Pass(e, c, "emperor-off");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-off");
            Check(e.Field.CurrentPlay is { Type: HandType.Emperor } && !e.Field.IsEmpty && rec.Flushes.Count == 0,
                "emperor-off: the emperor stays on the field");
            Check(rec.Effects.Contains(SpecialEffectType.Emperor), "emperor-off: the emperor is still announced");
            Check(rec.Asked.Count == 0 && !e.Field.IsSandstormStopPending, "emperor-off: no interrupt window");
            Check(e.PendingDecision is { Kind: DecisionKind.PlayOrPass } d && d.PlayerId == a && d.CanPass, "emperor-off: the next player decides");
            Pass(e, a, "emperor-off"); Pass(e, b, "emperor-off");
            Check(rec.Flushes.Count == 0 && e.PendingDecision!.PlayerId == c, "emperor-off: nothing flushes until everyone has passed");
            Pass(e, c, "emperor-off");
            Check(rec.Flushes.Count == 1 && rec.Flushes[0].Cause is null && rec.Flushes[0].NextLeaderId == l,
                "emperor-off: after all pass the field flushes normally and the emperor player leads");
        }

        // (2) 流さない＋エンペラーで革命：革命だけ起きて、場は続く
        {
            var r = EmperorRules(flushes: false, sandstorm: false);
            r.Sequence.EmperorCausesRevolution = true;
            var (e, l, a, b, c, rec) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S4 H5 D6 C7 SK"));
            e.GetPlayer(a).SetHand(C("C9 D10"));
            e.GetPlayer(b).SetHand(C("H9 D9"));
            e.GetPlayer(c).SetHand(C("DJ CQ"));
            Play(e, l, "D3", "emperor-off-rev");
            Pass(e, a, "emperor-off-rev"); Pass(e, b, "emperor-off-rev"); Pass(e, c, "emperor-off-rev");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-off-rev");
            Check(e.Field.IsRevolution && rec.Flushes.Count == 0 && e.PendingDecision!.PlayerId == a,
                "emperor-off-rev: the revolution applies and play continues");
        }

        // (3) 流す・砂嵐ルールなし：止める手段がないので、すぐに流れる
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: true, sandstorm: false));
            e.AlwaysAskInterruptPlayerIds.Add(a); // 初心者用の人がいても、止める手段がなければ聞かない
            e.GetPlayer(l).SetHand(C("D3 S4 H5 D6 C7 SK"));
            e.GetPlayer(a).SetHand(C("C9 D10"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D9"));
            e.GetPlayer(c).SetHand(C("DJ CQ"));
            Play(e, l, "D3", "emperor-on");
            Pass(e, a, "emperor-on"); Pass(e, b, "emperor-on"); Pass(e, c, "emperor-on");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-on");
            Check(rec.Asked.Count == 0 && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Emperor
                && e.PendingDecision!.PlayerId == l && e.Field.IsEmpty, "emperor-on without sandstorm: immediate flush, the emperor player leads");
        }

        // (4) 禁止あがり「流し役であがれない」：流さないエンペラーは流し役ではない
        {
            foreach (bool flushes in new[] { false, true })
            {
                var r = EmperorRules(flushes, sandstorm: false);
                r.ForbiddenFinish.Enabled = true;
                r.ForbiddenFinish.ForbidOtherFlushEffects = true;
                r.ForbiddenFinish.ForbidStrongestNumber = false;
                var j = new JudgeLogic(r);
                var judgement = j.Judge(C("S4 H5 D6 C7"), FieldState.CreateInitial(), isFinishingPlay: true);
                Check(judgement.IsLegal && judgement.Play!.Type == HandType.Emperor, $"emperor forbidden-finish ({flushes}): legal emperor");
                Check(judgement.IsForbiddenFinish == flushes && judgement.FlushesField == flushes,
                    $"emperor forbidden-finish: a {(flushes ? "flushing" : "non-flushing")} emperor {(flushes ? "is" : "is not")} a flush finish");
            }
        }
    }

    // ------------------------------------------------------------------
    // 場を流すエンペラーは、砂嵐で止められる
    // ------------------------------------------------------------------

    private static void EmperorStoppedBySandstorm()
    {
        void Deal(GameEngine e, int l, int a, int b, int c)
        {
            e.GetPlayer(l).SetHand(C("D3 S4 H5 D6 C7 SK"));
            e.GetPlayer(a).SetHand(C("C9 D10"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D9"));   // 砂嵐を持つ
            e.GetPlayer(c).SetHand(C("DJ CQ"));
        }

        // 止める
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: true, sandstorm: true));
            Deal(e, l, a, b, c);
            Play(e, l, "D3", "emperor-stop");
            Pass(e, a, "emperor-stop"); Pass(e, b, "emperor-stop"); Pass(e, c, "emperor-stop");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-stop");
            Check(rec.Asked.SequenceEqual(new[] { b }) && e.PendingDecision is { Kind: DecisionKind.SandstormStopChance } d
                && d.Effect == SpecialEffectType.Emperor && d.TargetPlayerId == l && rec.Flushes.Count == 0,
                "emperor-stop: only the sandstorm holder is asked before the flush");
            var legal = e.GetLegalPlays(b);
            Check(legal.Count == 1 && legal[0].BeatMethod == BeatMethod.SandstormStop, "emperor-stop: the 3-card sandstorm stops a 4-card emperor");
            Play(e, b, "S3 H3 C3", "emperor-stop");
            Check(!rec.Effects.Contains(SpecialEffectType.Emperor) && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Sandstorm
                && e.PendingDecision!.PlayerId == b, "emperor-stop: the emperor flush is cancelled, the sandstorm flushes and the stopper leads");
        }

        // 見送る
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: true, sandstorm: true));
            Deal(e, l, a, b, c);
            Play(e, l, "D3", "emperor-decline");
            Pass(e, a, "emperor-decline"); Pass(e, b, "emperor-decline"); Pass(e, c, "emperor-decline");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-decline");
            Pass(e, b, "emperor-decline");
            Check(rec.Effects.Contains(SpecialEffectType.Emperor) && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Emperor
                && e.PendingDecision!.PlayerId == l, "emperor-decline: the emperor flushes and its player leads");
        }

        // 誰も砂嵐を持っていない：確認なしですぐ流れる。初心者用の人だけは聞かれる
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: true, sandstorm: true));
            Deal(e, l, a, b, c);
            e.GetPlayer(b).SetHand(C("S3 H3 D9"));
            Play(e, l, "D3", "emperor-nobody");
            Pass(e, a, "emperor-nobody"); Pass(e, b, "emperor-nobody"); Pass(e, c, "emperor-nobody");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-nobody");
            Check(rec.Asked.Count == 0 && rec.Flushes.Count == 1 && e.PendingDecision!.PlayerId == l, "emperor-nobody: immediate flush");
        }
        {
            var (e, l, a, b, c, rec) = Start(EmperorRules(flushes: true, sandstorm: true));
            e.AlwaysAskInterruptPlayerIds.Add(c);
            Deal(e, l, a, b, c);
            e.GetPlayer(b).SetHand(C("S3 H3 D9"));
            Play(e, l, "D3", "emperor-beginner");
            Pass(e, a, "emperor-beginner"); Pass(e, b, "emperor-beginner"); Pass(e, c, "emperor-beginner");
            rec.Clear();
            Play(e, l, "S4 H5 D6 C7", "emperor-beginner");
            Check(rec.Asked.SequenceEqual(new[] { c }) && e.GetLegalPlays(c).Count == 0 && rec.Flushes.Count == 0,
                "emperor-beginner: the beginner is asked even without a sandstorm");
            Pass(e, c, "emperor-beginner");
            Check(rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Emperor, "emperor-beginner: flush after the beginner passes");
        }
    }

    // ------------------------------------------------------------------
    // 8切りは4止めでも砂嵐でも止められる
    // ------------------------------------------------------------------

    private static void EightCutStoppedBySandstorm()
    {
        RuleSettings Rules(bool fourStop)
        {
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = fourStop;
            r.Flow.SandstormEnabled = true;
            return r;
        }

        // 4止めも砂嵐も有効：4のペアを持つ a、砂嵐を持つ b の順に聞く。a が見送り、b が砂嵐で止める
        {
            var (e, l, a, b, c, rec) = Start(Rules(fourStop: true));
            e.GetPlayer(l).SetHand(C("D3 H8 SK"));
            e.GetPlayer(a).SetHand(C("S4 C4 C5"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D7"));
            e.GetPlayer(c).SetHand(C("D9 CK"));
            Play(e, l, "D3", "8cut-both");
            Pass(e, a, "8cut-both"); Pass(e, b, "8cut-both"); Pass(e, c, "8cut-both");
            rec.Clear();
            Play(e, l, "H8", "8cut-both");
            Check(e.Field.IsEightCutPending && e.Field.IsSandstormStopPending && rec.Flushes.Count == 0, "8cut-both: both stoppers are open");
            Check(e.PendingDecision is { Kind: DecisionKind.FourStopChance } d && d.PlayerId == a && d.Effect == SpecialEffectType.EightCut,
                "8cut-both: the 4-pair holder is asked first");
            Check(e.GetLegalPlays(a).Any(p => p.BeatMethod == BeatMethod.FourStop), "8cut-both: the 4-pair is a legal stop");
            Pass(e, a, "8cut-both");
            Check(e.PendingDecision is { Kind: DecisionKind.FourStopChance } d2 && d2.PlayerId == b, "8cut-both: then the sandstorm holder");
            var legal = e.GetLegalPlays(b);
            Check(legal.Count == 1 && legal[0].BeatMethod == BeatMethod.SandstormStop, "8cut-both: the sandstorm is the only legal stop for b");
            Play(e, b, "S3 H3 C3", "8cut-both");
            Check(rec.Asked.SequenceEqual(new[] { a, b }) && !rec.Effects.Contains(SpecialEffectType.EightCut)
                && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Sandstorm && e.PendingDecision!.PlayerId == b,
                "8cut-both: the sandstorm cancels the 8-cut and the stopper leads");
        }

        // 4止めが無効：砂嵐の確認として聞く
        {
            var (e, l, a, b, c, rec) = Start(Rules(fourStop: false));
            e.GetPlayer(l).SetHand(C("D3 H8 SK"));
            e.GetPlayer(a).SetHand(C("S4 C4 C5"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D7"));
            e.GetPlayer(c).SetHand(C("D9 CK"));
            Play(e, l, "D3", "8cut-sand");
            Pass(e, a, "8cut-sand"); Pass(e, b, "8cut-sand"); Pass(e, c, "8cut-sand");
            rec.Clear();
            Play(e, l, "H8", "8cut-sand");
            Check(!e.Field.IsEightCutPending && e.Field.IsSandstormStopPending
                && e.PendingDecision is { Kind: DecisionKind.SandstormStopChance } d && d.PlayerId == b,
                "8cut-sand: without 4-stop only the sandstorm holder is asked");
            Check(e.SubmitPlay(b, C("D7")).Error == CommandError.IllegalPlay, "8cut-sand: nothing but the sandstorm can be played");
            Pass(e, b, "8cut-sand");
            Check(rec.Effects.Contains(SpecialEffectType.EightCut) && rec.Flushes.Count == 1 && e.PendingDecision!.PlayerId == l,
                "8cut-sand: declined -> the 8-cut flushes");
        }

        // 階段の8切り：4では止められないが、砂嵐なら止められる
        {
            var r = Rules(fourStop: true);
            r.Sequence.EffectsInSequence = true;
            var (e, l, a, b, c, rec) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 H7 H8 H9 SK"));
            e.GetPlayer(a).SetHand(C("S4 H4 C4 D4 C5"));
            e.GetPlayer(b).SetHand(C("S3 H3 C3 D7"));
            e.GetPlayer(c).SetHand(C("D9 CK"));
            Play(e, l, "D3", "8cut-seq");
            Pass(e, a, "8cut-seq"); Pass(e, b, "8cut-seq"); Pass(e, c, "8cut-seq");
            rec.Clear();
            Play(e, l, "H7 H8 H9", "8cut-seq");
            Check(rec.Asked.SequenceEqual(new[] { b }) && rec.AskedKinds.SequenceEqual(new[] { DecisionKind.SandstormStopChance })
                && !e.Field.IsEightCutPending, "8cut-seq: the 4 holder is not asked, the sandstorm holder is");
            Play(e, b, "S3 H3 C3", "8cut-seq");
            Check(rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Sandstorm, "8cut-seq: the sandstorm stops a sequence 8-cut");
        }

        // 誰も止められない8切り：確認なしですぐ流れる（4止め・砂嵐とも有効でも）
        {
            var (e, l, a, b, c, rec) = Start(Rules(fourStop: true));
            e.GetPlayer(l).SetHand(C("D3 H8 SK"));
            e.GetPlayer(a).SetHand(C("S4 C5"));
            e.GetPlayer(b).SetHand(C("S3 H3 D7"));
            e.GetPlayer(c).SetHand(C("D9 CK"));
            Play(e, l, "D3", "8cut-nobody");
            Pass(e, a, "8cut-nobody"); Pass(e, b, "8cut-nobody"); Pass(e, c, "8cut-nobody");
            rec.Clear();
            Play(e, l, "H8", "8cut-nobody");
            Check(rec.Asked.Count == 0 && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.EightCut
                && e.PendingDecision!.PlayerId == l, "8cut-nobody: immediate flush");
        }
    }

    // ------------------------------------------------------------------
    // 砂嵐は誰にも止められない（初心者用の人がいても確認しない）
    // ------------------------------------------------------------------

    private static void SandstormIsNeverInterrupted()
    {
        var r = RuleSettings.CreateDefault();
        r.Flow.SandstormEnabled = true;
        r.Flow.FourStopEnabled = true;
        var (e, l, a, b, c, rec) = Start(r);
        foreach (int id in new[] { a, b, c }) e.AlwaysAskInterruptPlayerIds.Add(id);
        e.GetPlayer(l).SetHand(C("D3 S3 H3 C3 SK"));
        e.GetPlayer(a).SetHand(C("S4 C4 C5"));
        e.GetPlayer(b).SetHand(C("D7 D8"));
        e.GetPlayer(c).SetHand(C("D9 CK"));
        Play(e, l, "D3", "sandstorm");
        Pass(e, a, "sandstorm"); Pass(e, b, "sandstorm"); Pass(e, c, "sandstorm");
        rec.Clear();
        Play(e, l, "S3 H3 C3", "sandstorm");
        Check(rec.Asked.Count == 0 && rec.Flushes.Count == 1 && rec.Flushes[0].Cause == SpecialEffectType.Sandstorm
            && e.PendingDecision!.PlayerId == l && !e.Field.IsSandstormStopPending,
            "sandstorm: flushes at once with no interrupt prompt, even for beginners");
    }

    // ------------------------------------------------------------------
    // 判定：8切りの割り込み中は、4止めと砂嵐の両方を受け付ける
    // ------------------------------------------------------------------

    private static void JudgeAcceptsBothStoppers()
    {
        var r = RuleSettings.CreateDefault();
        r.Flow.FourStopEnabled = true;
        r.Flow.SandstormEnabled = true;
        var j = new JudgeLogic(r);
        var eight = j.Judge(C("H8"), FieldState.CreateInitial()).Play!;
        var both = FieldState.CreateInitial().WithPlay(1, eight, LockState.None).WithEightCutPending(true).WithSandstormStopPending(true);
        Check(j.Judge(C("S4 C4"), both) is { IsLegal: true, BeatMethod: BeatMethod.FourStop }, "judge: the 4-pair stops the 8-cut");
        Check(j.Judge(C("S3 H3 C3"), both) is { IsLegal: true, BeatMethod: BeatMethod.SandstormStop }, "judge: the sandstorm stops the 8-cut");
        Check(j.Judge(C("S9"), both).RejectReason == PlayRejectReason.FourStopOnly, "judge: other cards are rejected");
        var legal = j.GetLegalPlays(C("S4 C4 S3 H3 C3 D9"), both);
        Check(legal.Any(p => p.BeatMethod == BeatMethod.FourStop) && legal.Any(p => p.BeatMethod == BeatMethod.SandstormStop)
            && legal.All(p => p.BeatMethod is BeatMethod.FourStop or BeatMethod.SandstormStop), "judge: legal plays list both stoppers only");

        var onlyFour = FieldState.CreateInitial().WithPlay(1, eight, LockState.None).WithEightCutPending(true);
        Check(!j.Judge(C("S3 H3 C3"), onlyFour).IsLegal, "judge: the sandstorm is refused when only the 4-stop window is open");
    }

    // ------------------------------------------------------------------
    // 操作ガイドのヒント（初心者用：毎回／中級者用：初めてのときだけ／上級者用：なし）
    // ------------------------------------------------------------------

    private static void GuideHintLevels()
    {
        Check(new AppearanceSettings().GuideLevel == GuideLevel.Intermediate, "guide: intermediate by default");

        var beginner = new GuideHints(GuideLevel.Beginner);
        var middle = new GuideHints(GuideLevel.Intermediate);
        var expert = new GuideHints(GuideLevel.Expert);

        Check(beginner.ForEffect(SpecialEffectType.EightCut) is not null && beginner.ForEffect(SpecialEffectType.EightCut) is not null,
            "guide beginner: effect hints every time");
        Check(middle.ForEffect(SpecialEffectType.EightCut) is not null && middle.ForEffect(SpecialEffectType.EightCut) is null,
            "guide intermediate: an effect hint only the first time");
        Check(middle.ForEffect(SpecialEffectType.Revolution) is not null && middle.ForEffect(SpecialEffectType.Coup) is null,
            "guide intermediate: revolution kinds share one first-time hint");
        Check(expert.ForEffect(SpecialEffectType.Revolution) is null && expert.ForCardSelection(CardSelectionPurpose.SevenPass) is null
            && expert.ForTurn(true, true) is null, "guide expert: no hints at all");
        Check(beginner.ForEffect(SpecialEffectType.Revolution, applied: false) is null, "guide: no hint for an effect that did not apply");

        Check(middle.ForCardSelection(CardSelectionPurpose.TenDiscard) is not null && middle.ForCardSelection(CardSelectionPurpose.TenDiscard) is null,
            "guide intermediate: card selection hint the first time only");
        Check(beginner.ForCardSelection(CardSelectionPurpose.TenDiscard) is not null && beginner.ForCardSelection(CardSelectionPurpose.TenDiscard) is not null,
            "guide beginner: card selection hint every time");

        Check(middle.ForTurn(true, true) is null, "guide intermediate: no per-turn tutorial");
        Check(beginner.ForTurn(true, true)!.Contains("親"), "guide beginner: lead hint");
        Check(beginner.ForTurn(false, false)!.Contains("パス"), "guide beginner: no-legal-play hint suggests passing");
        string? locked = beginner.ForTurn(false, true, reversed: true, suitLocked: true);
        Check(locked is not null && locked.Contains("逆転") && locked.Contains("スート縛り"), "guide beginner: follow hint mentions reversal and suit lock");

        middle.Reset();
        Check(middle.ForEffect(SpecialEffectType.EightCut) is not null, "guide intermediate: reset for a new game shows first-time hints again");

        foreach (var type in Enum.GetValues<SpecialEffectType>())
        {
            Check(GuideHints.EffectText(type) is { Length: > 0 }, "guide: every effect has an explanation: " + type);
        }
        foreach (var level in GuideLevelInfo.All)
        {
            Check(level.DisplayName().Length > 0 && level.Description().Length > 0, "guide: level has a name and description: " + level);
        }
    }
}
