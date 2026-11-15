using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

internal static class EngineTests
{
    private static int _pass;
    private static int _fail;
    private static readonly Dictionary<string, int> Coverage = new();

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(engine): " + name); }
    }

    private static void Cover(string key) => Coverage[key] = Coverage.TryGetValue(key, out var n) ? n + 1 : 1;

    private static List<PlayerSetup> Players(int n)
        => Enumerable.Range(0, n).Select(i => new PlayerSetup("P" + i, i == 0 ? PlayerKind.Human : PlayerKind.Npc)).ToList();

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

    /// <summary>指定したプレイヤーを操作ガイド「初心者用」扱いにする（止められる手がなくても割り込みの確認を受ける）。</summary>
    private static void AskAll(GameEngine e, params int[] ids)
    {
        foreach (int id in ids) e.AlwaysAskInterruptPlayerIds.Add(id);
    }

    /// <summary>
    /// 第1ラウンドを手札仕込みで即終了させる。座席順は 0,1,2,3 固定（席替えなし）。
    /// 結果：L=大富豪, L+1=富豪, L+2=貧民, L+3=大貧民。
    /// </summary>
    private static int QuickFirstRound(GameEngine e)
    {
        e.StartGame();
        int l = e.PendingDecision!.PlayerId;
        e.GetPlayer(l).SetHand(C("D3"));
        e.GetPlayer((l + 1) % 4).SetHand(C("S5"));
        e.GetPlayer((l + 2) % 4).SetHand(C("S6"));
        e.GetPlayer((l + 3) % 4).SetHand(C("S7"));
        Play(e, l, "D3", "r1");
        Play(e, (l + 1) % 4, "S5", "r1");
        Play(e, (l + 2) % 4, "S6", "r1");
        return l;
    }

    private static void Scenarios()
    {
        // ---- 都落ち ----
        {
            var r = RuleSettings.CreateDefault();
            r.Exchange.Enabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(1));
            int l = QuickFirstRound(e);
            Check(e.Phase == GamePhase.RoundEnded && e.GetPlayer(l).Rank == PlayerRank.Daifugo
                && e.GetPlayer((l + 3) % 4).Rank == PlayerRank.Daihinmin, "r1 ranks");
            e.StartNextRound();
            int poor = (l + 3) % 4;
            Check(e.PendingDecision!.PlayerId == poor, "r2 daihinmin leads");
            e.GetPlayer(poor).SetHand(C("S4"));
            e.GetPlayer(l).SetHand(C("S9 H9"));
            e.GetPlayer((l + 1) % 4).SetHand(C("S10"));
            e.GetPlayer((l + 2) % 4).SetHand(C("SK"));
            Play(e, poor, "S4", "miyako");
            Check(e.GetPlayer(l).IsFinished && e.GetPlayer(l).HandCount == 0, "miyako: former daifugo out");
            Check(e.PendingDecision!.PlayerId == (l + 1) % 4, "miyako: turn skips fallen player");
            Play(e, (l + 1) % 4, "S10", "miyako");
            var st = e.RoundResults[^1].Standings;
            Check(st[0].PlayerId == poor && st[3].PlayerId == l && st[3].Kind == FinishKind.MiyakoOchi, "miyako standings");
            Check(e.RoundResults[^1].MiyakoOchiPlayerId == l, "miyako id");
        }

        // ---- 下剋上 ----
        {
            var r = RuleSettings.CreateDefault();
            r.Exchange.Enabled = false;
            r.RankChange.GekokujoEnabled = true;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(2));
            int l = QuickFirstRound(e);
            e.StartNextRound();
            int poor = (l + 3) % 4;
            e.GetPlayer(poor).SetHand(C("S4"));
            Play(e, poor, "S4", "gekokujo");
            var res = e.RoundResults[^1];
            Check(e.Phase == GamePhase.RoundEnded && res.IsGekokujo, "gekokujo ends round");
            Check(e.GetPlayer(poor).Rank == PlayerRank.Daifugo && e.GetPlayer(l).Rank == PlayerRank.Daihinmin
                && e.GetPlayer((l + 1) % 4).Rank == PlayerRank.Hinmin && e.GetPlayer((l + 2) % 4).Rank == PlayerRank.Fugo, "gekokujo inverted");
        }

        // ---- カード交換（最強カード自動・ジョーカー保護） ----
        int jokerKeptCases = 0;
        for (int seed = 0; seed < 80; seed++)
        {
            foreach (bool protect in new[] { false, true })
            {
                var r = RuleSettings.CreateDefault();
                r.Exchange.JokerProtected = protect;
                var e = new GameEngine(r, Players(4), new SystemRandomSource(seed));
                var transfers = new List<CardsTransferredEvent>();
                e.EventOccurred += (_, ev) => { if (ev is CardsTransferredEvent t) transfers.Add(t); };
                int l = QuickFirstRound(e);
                int poor = (l + 3) % 4;
                e.StartNextRound();
                Check(e.Phase == GamePhase.Exchanging, "exchange phase");
                var given = transfers.Single();
                Check(given.FromPlayerId == poor && given.ToPlayerId == l && given.Cards.Count == 2, "poor gives 2 to daifugo");
                var remaining = e.GetPlayer(poor).Hand.Where(c => !(protect && c.IsJoker)).ToList();
                bool strongest = given.Cards.All(g => remaining.All(c => g.BaseStrength >= c.BaseStrength));
                Check(strongest, $"strongest given seed={seed} protect={protect}");
                if (protect)
                {
                    Check(given.Cards.All(c => !c.IsJoker), "joker protected");
                    if (e.GetPlayer(poor).Hand.Any(c => c.IsJoker)) jokerKeptCases++;
                }
                var d = e.PendingDecision!;
                Check(d.Kind == DecisionKind.SelectCards && d.PlayerId == l && d.MinCount == 2 && d.TargetPlayerId == poor, "daifugo returns 2");
                Check(e.SubmitCardSelection(l, e.GetPlayer(l).Hand.Take(1)).Error == CommandError.InvalidSelection, "wrong count");
                Check(e.SubmitCardSelection(l, e.GetPlayer(l).Hand.Take(2).ToList()).IsAccepted, "return cards");
                Check(e.PendingDecision!.PlayerId == (l + 1) % 4 && transfers.Count == 3, "fugo returns next");
                e.SubmitCardSelection((l + 1) % 4, e.GetPlayer((l + 1) % 4).Hand.Take(1).ToList());
                Check(e.Phase == GamePhase.Playing && e.PendingDecision!.PlayerId == poor, "play after exchange");
                Check(e.Players.Sum(p => p.HandCount) == 53, "exchange conserves");
            }
        }
        Check(jokerKeptCases > 0, "joker protection exercised");

        // ---- 4止め（標準：8の枚数の2倍。8が1枚なら4を2枚）----
        {
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            Check(r.Flow.FourStopCount == FourStopCountRule.DoubleEight, "4stop: double count is the default");
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
            e.GetPlayer(l).SetHand(C("D3 H8 S9"));
            e.GetPlayer(a).SetHand(C("S4 C5"));           // 4が1枚だけ → 8×1 は止められない
            e.GetPlayer(b).SetHand(C("H4 C4 C6"));        // 4が2枚 → 止められる
            e.GetPlayer(c).SetHand(C("D7 CK"));
            Play(e, l, "D3", "4stop");
            Pass(e, a, "4stop"); Pass(e, b, "4stop"); Pass(e, c, "4stop");
            Play(e, l, "H8", "4stop");
            Check(e.PendingDecision!.Kind == DecisionKind.FourStopChance && e.PendingDecision.PlayerId == b,
                "4stop: a single 4 cannot stop a single 8, so only the pair holder is asked");
            var legal = e.GetLegalPlays(b);
            Check(legal.Count == 1 && legal[0].Play!.Count == 2 && legal[0].BeatMethod == BeatMethod.FourStop, "4stop: the 4-pair is the only legal stop");
            Check(e.SubmitPlay(b, C("H4")).Error == CommandError.IllegalPlay, "4stop: one 4 over one 8 is rejected");
            Play(e, b, "H4 C4", "4stop");
            Check(e.Field.CurrentPlay!.Rank == Rank.Four && e.Field.CurrentPlay.Count == 2 && !e.Field.IsEightCutPending, "4stop: the 4-pair stays on the field");
            Check(e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == c, "4stop: turn continues after the stopper");
            Check(e.GetLegalPlays(c).Count == 0, "4stop: the next player now needs a stronger pair");
        }
        {
            // 8と同じ枚数の設定：4を1枚で止められる
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            r.Flow.FourStopCount = FourStopCountRule.SameAsEight;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4, b = (l + 2) % 4;
            e.GetPlayer(l).SetHand(C("D3 H8 S9"));
            e.GetPlayer(a).SetHand(C("S4 C5"));
            e.GetPlayer(b).SetHand(C("H6 C6"));
            e.GetPlayer((l + 3) % 4).SetHand(C("D7 CK"));
            Play(e, l, "D3", "4stop-same");
            Pass(e, a, "4stop-same"); Pass(e, b, "4stop-same"); Pass(e, (l + 3) % 4, "4stop-same");
            Play(e, l, "H8", "4stop-same");
            Check(e.PendingDecision!.Kind == DecisionKind.FourStopChance && e.PendingDecision.PlayerId == a, "4stop-same: one 4 stops one 8");
            Play(e, a, "S4", "4stop-same");
            Check(e.Field.CurrentPlay!.Rank == Rank.Four && !e.Field.IsEightCutPending && e.PendingDecision!.PlayerId == b, "4stop-same: play continues");
        }
        {
            // 8が2枚なら4は4枚。4を4枚出すと、革命の条件も満たすので革命も起きる
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            r.Lock.SuitLockEnabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            var effects = new List<SpecialEffectType>();
            e.EventOccurred += (_, ev) => { if (ev is EffectAppliedEvent ea && ea.Applied) effects.Add(ea.Effect.Type); };
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
            e.GetPlayer(l).SetHand(C("D3 C3 H8 S8 S9"));
            e.GetPlayer(a).SetHand(C("D6 C7"));
            e.GetPlayer(b).SetHand(C("S5 H5 C6"));
            e.GetPlayer(c).SetHand(C("D4 C4 H4 S4 DK"));           // 4が4枚（2枚だけ出しても止められない）
            Play(e, l, "D3 C3", "4stop-x2");
            Pass(e, a, "4stop-x2"); Pass(e, b, "4stop-x2"); Pass(e, c, "4stop-x2");
            Play(e, l, "H8 S8", "4stop-x2");
            Check(e.PendingDecision!.Kind == DecisionKind.FourStopChance && e.PendingDecision.PlayerId == c, "4stop-x2: only the 4-quad holder is asked");
            Check(e.SubmitPlay(c, C("D4 C4")).Error == CommandError.IllegalPlay, "4stop-x2: two 4s over two 8s are rejected");
            Play(e, c, "D4 C4 H4 S4", "4stop-x2");
            Check(effects.Contains(SpecialEffectType.FourStop) && !effects.Contains(SpecialEffectType.EightCut), "4stop-x2: the 8-cut is cancelled");
            Check(effects.Contains(SpecialEffectType.Revolution) && e.Field.IsRevolution, "4stop-x2: four 4s also start a revolution");
            Check(e.Field.CurrentPlay!.Count == 4 && !e.Field.IsEightCutPending, "4stop-x2: the quad stays on the field");
        }
        {
            // 階段に含まれる8の8切りは、4では止められない（そのまま流れる）
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            r.Sequence.EffectsInSequence = true;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4;
            e.GetPlayer(l).SetHand(C("D3 H7 H8 H9 SK"));
            e.GetPlayer(a).SetHand(C("S4 H4 C4 D4 JK0 C5"));
            e.GetPlayer((l + 2) % 4).SetHand(C("C6 D6"));
            e.GetPlayer((l + 3) % 4).SetHand(C("D7 CK"));
            Play(e, l, "D3", "4stop-seq");
            Pass(e, a, "4stop-seq"); Pass(e, (l + 2) % 4, "4stop-seq"); Pass(e, (l + 3) % 4, "4stop-seq");
            Play(e, l, "H7 H8 H9", "4stop-seq");
            Check(e.Field.IsEmpty && e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == l,
                "4stop-seq: an 8 inside a sequence cannot be stopped");
        }
        {
            // 4止めを見送れば8切り成立
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4;
            e.GetPlayer(l).SetHand(C("D3 H8 S9"));
            e.GetPlayer(a).SetHand(C("S4 C4 C5"));
            e.GetPlayer((l + 2) % 4).SetHand(C("H6 C6"));
            e.GetPlayer((l + 3) % 4).SetHand(C("D7 CK"));
            Play(e, l, "D3", "8cut");
            Pass(e, a, "8cut"); Pass(e, (l + 2) % 4, "8cut"); Pass(e, (l + 3) % 4, "8cut");
            Play(e, l, "H8", "8cut");
            Pass(e, a, "8cut decline");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == l, "8cut flush, cutter leads");
        }

        NewRuleScenarios();

        // ---- スキップ（パス固定モード）----
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.PassLocksUntilFlush = true;
            r.SkipReverse.FiveSkipEnabled = true;
            r.Lock.SuitLockEnabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(4));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
            e.GetPlayer(l).SetHand(C("D3 C4"));
            e.GetPlayer(a).SetHand(C("H5 C6"));
            e.GetPlayer(b).SetHand(C("H9 S10"));
            e.GetPlayer(c).SetHand(C("S7 DQ"));
            Play(e, l, "D3", "skip");
            Play(e, a, "H5", "skip");
            Check(e.PendingDecision!.PlayerId == c, "b skipped");
            Play(e, c, "S7", "skip");
            Pass(e, l, "skip");
            Pass(e, a, "skip");
            Check(e.PendingDecision!.PlayerId == b, "skipped player may play again after next play");
            Check(!e.Field.HasPassed(b), "skip not recorded as pass");
            Pass(e, b, "skip");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == c, "flush after locked passes");
        }

        // ---- パス制限 ----
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.PassLimitPerRound = 1;
            r.Lock.SuitLockEnabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(5));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4;
            e.GetPlayer(l).SetHand(C("D3 C4 C5"));
            e.GetPlayer(a).SetHand(C("HK SK"));
            e.GetPlayer((l + 2) % 4).SetHand(C("H6 C6"));
            e.GetPlayer((l + 3) % 4).SetHand(C("D7 C7"));
            Play(e, l, "D3", "passlimit");
            Pass(e, a, "passlimit"); Pass(e, (l + 2) % 4, "passlimit"); Pass(e, (l + 3) % 4, "passlimit");
            Play(e, l, "C4", "passlimit");
            Check(!e.PendingDecision!.CanPass, "cannot pass flag");
            Check(e.SubmitPass(a).Error == CommandError.PassLimitReached, "pass limit reached");
        }

        // ---- 延長戦 ----
        foreach (int maxExtra in new[] { 0, 1, 2 })
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.RoundCount = 1;
            r.Progress.MaxExtraRounds = maxExtra;
            r.Progress.PointsDaifugo = r.Progress.PointsFugo = r.Progress.PointsHinmin = r.Progress.PointsDaihinmin = 0; // 常に同点
            var e = new GameEngine(r, Players(4), new SystemRandomSource(6));
            AttachBot(e, new Random(6));
            e.StartGame();
            Check(e.Phase == GamePhase.GameEnded && e.RoundNumber == 1 + maxExtra, $"extra rounds max={maxExtra} played={e.RoundNumber}");
        }
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.RoundCount = 1;
            r.Progress.ExtraRoundOnTie = false;
            r.Progress.PointsDaifugo = r.Progress.PointsFugo = r.Progress.PointsHinmin = r.Progress.PointsDaihinmin = 0;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(6));
            GameResult? result = null;
            e.EventOccurred += (_, ev) => { if (ev is GameEndedEvent g) result = g.Result; };
            AttachBot(e, new Random(6));
            e.StartGame();
            Check(e.RoundNumber == 1 && result!.WinnerIds.Count == 4, "no extra round -> shared win");
        }

        // ---- 配牌の偏り（14枚手札が特定席に固定されない）----
        {
            var counts = new int[4];
            for (int seed = 0; seed < 40; seed++)
            {
                var e = new GameEngine(RuleSettings.CreateDefault(), Players(4), new SystemRandomSource(seed));
                e.StartGame();
                for (int i = 0; i < 4; i++) if (e.GetPlayer(i).HandCount == 14) counts[i]++;
            }
            Check(counts.All(n => n > 0), "14-card hand distributed: " + string.Join(",", counts));
        }
    }

    /// <summary>
    /// 11バックの解除条件・12ボンバーのジョーカー・砂嵐の割り込み・3枚縛り・2枚階段の筋書きテスト。
    /// </summary>
    private static void NewRuleScenarios()
    {
        // 親 l から時計回りに a, b, c。第1ラウンドは♦3を持つ l が親
        static (GameEngine E, int L, int A, int B, int C) Start(RuleSettings r, int seed = 11)
        {
            r.Exchange.Enabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(seed));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            return (e, l, (l + 1) % 4, (l + 2) % 4, (l + 3) % 4);
        }

        // ---- 11バック：通常は場が流れた時点で解除される ----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5 S9"));
            e.GetPlayer(a).SetHand(C("HJ C6 C9"));
            e.GetPlayer(b).SetHand(C("H7 C7 DK"));
            e.GetPlayer(c).SetHand(C("D4 CK HK"));
            Play(e, l, "D3", "11back");
            Play(e, a, "HJ", "11back");
            Check(e.Field.IsElevenBack && !e.Field.IsElevenBackPersistent && e.Field.IsStrengthReversed, "11back on, not persistent");
            Check(e.SubmitPlay(b, C("DK")).Error == CommandError.IllegalPlay, "11back: stronger card rejected while reversed");
            Play(e, b, "H7", "11back");
            Pass(e, c, "11back"); Pass(e, l, "11back"); Pass(e, a, "11back");
            Check(e.Field.IsEmpty && !e.Field.IsElevenBack && !e.Field.IsStrengthReversed, "11back cleared by flush");
            Check(e.PendingDecision!.PlayerId == b, "11back: last player leads");
            // 解除後は通常の強弱に戻っている
            Play(e, b, "C7", "11back");
            Check(e.SubmitPlay(c, C("D4")).Error == CommandError.IllegalPlay, "11back: weak card rejected after flush");
            Play(e, c, "CK", "11back");
        }
        // ---- 11バック：誰も続かず全員パスで流れた場合も解除される ----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5 S9"));
            e.GetPlayer(a).SetHand(C("HJ C6 C9"));
            e.GetPlayer(b).SetHand(C("H7 C7 D9"));
            e.GetPlayer(c).SetHand(C("D4 CK HK"));
            Play(e, l, "D3", "11back-8cut");
            Play(e, a, "HJ", "11back-8cut");
            Check(e.Field.IsElevenBack, "11back on (2)");
            Pass(e, b, "11back-8cut"); Pass(e, c, "11back-8cut"); Pass(e, l, "11back-8cut");
            Check(!e.Field.IsElevenBack && e.PendingDecision!.PlayerId == a, "11back cleared when everyone passes");
        }
        // ---- 強化11バック：場が流れても続き、次のJで解除、新しいラウンドでは必ずリセット ----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.StrongElevenBackEnabled = true;
            r.RankChange.MiyakoOchiEnabled = false;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5 SJ S9"));
            e.GetPlayer(a).SetHand(C("HJ C6 C9"));
            e.GetPlayer(b).SetHand(C("H7 C7 D9"));
            e.GetPlayer(c).SetHand(C("D4 CK HK"));
            Play(e, l, "D3", "strong11");
            Play(e, a, "HJ", "strong11");
            Check(e.Field.IsElevenBack && e.Field.IsElevenBackPersistent, "strong 11back on");
            Pass(e, b, "strong11"); Pass(e, c, "strong11"); Pass(e, l, "strong11");
            Check(e.Field.IsEmpty && e.Field.IsElevenBack && e.Field.IsElevenBackPersistent && e.Field.IsStrengthReversed,
                "strong 11back survives the flush");
            Play(e, a, "C9", "strong11");
            Pass(e, b, "strong11"); Pass(e, c, "strong11");
            // 反転中なので 9 に対して J は弱い → 出せない。場が流れてから J で解除する
            Check(e.SubmitPlay(l, C("SJ")).Error == CommandError.IllegalPlay, "strong 11back: J is weaker than 9 while reversed");
            Pass(e, l, "strong11");
            Check(e.Field.IsElevenBack && e.PendingDecision!.PlayerId == a, "strong 11back still on after second flush");
            Play(e, a, "C6", "strong11"); // a あがり（1位）
            Check(e.GetPlayer(a).IsFinished, "strong11: a finished");
            Pass(e, b, "strong11"); Pass(e, c, "strong11");
            Play(e, l, "S5", "strong11");
            Pass(e, b, "strong11"); Pass(e, c, "strong11");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == l && e.Field.IsElevenBack, "strong11: l leads, still reversed");
            Play(e, l, "SJ", "strong11");
            Check(!e.Field.IsElevenBack && !e.Field.IsElevenBackPersistent && !e.Field.IsStrengthReversed, "strong 11back released by the next J");
            // もう一度 J 相当の反転を掛けた状態でラウンドを終わらせ、次のラウンドに持ち越さないことを確認する
            Pass(e, b, "strong11"); Pass(e, c, "strong11");
            Play(e, l, "S9", "strong11"); // l あがり（2位）
            Pass(e, b, "strong11");
            Play(e, c, "CK", "strong11");
            Pass(e, b, "strong11");
            Play(e, c, "HK", "strong11");
            Pass(e, b, "strong11");
            Play(e, c, "D4", "strong11"); // c あがり → b が最後
            Check(e.Phase == GamePhase.RoundEnded, "strong11: round ended");
        }
        {
            // 強化11バックが掛かったままラウンドが終わっても、次のラウンドは通常の強弱から始まる
            var r = RuleSettings.CreateDefault();
            r.ElevenBack.StrongElevenBackEnabled = true;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3"));
            e.GetPlayer(a).SetHand(C("SJ"));
            e.GetPlayer(b).SetHand(C("S9"));
            e.GetPlayer(c).SetHand(C("S7 H5"));
            Play(e, l, "D3", "strong11-round");
            Play(e, a, "SJ", "strong11-round");
            Check(e.Phase == GamePhase.Playing && e.Field.IsElevenBack && e.Field.IsElevenBackPersistent, "strong 11back on at round end");
            Play(e, b, "S9", "strong11-round");
            Check(e.Phase == GamePhase.RoundEnded, "strong11-round: ended");
            e.StartNextRound();
            Check(!e.Field.IsElevenBack && !e.Field.IsElevenBackPersistent && !e.Field.IsStrengthReversed && !e.Field.IsRevolution,
                "strong 11back reset at the next round");
        }

        // ---- 11バック「次の手番だけ」：Jの次の人がパスしたら、その次の人からは通常の強さ ----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly;
            var (e, l, a, b, c) = Start(r);
            var expired = new List<int>();
            e.EventOccurred += (_, ev) => { if (ev is ElevenBackExpiredEvent x) expired.Add(x.PlayerId); };
            e.GetPlayer(l).SetHand(C("D3 S5 S9"));
            e.GetPlayer(a).SetHand(C("HJ C6 C9"));
            e.GetPlayer(b).SetHand(C("H7 C7 DK"));
            e.GetPlayer(c).SetHand(C("D4 CK HK"));
            Play(e, l, "D3", "1turn-pass");
            Play(e, a, "HJ", "1turn-pass");
            Check(e.Field.IsElevenBack && e.Field.IsElevenBackOneTurn && !e.Field.IsElevenBackPersistent && e.Field.IsStrengthReversed,
                "one-turn 11back on");
            Check(e.SubmitPlay(b, C("DK")).Error == CommandError.IllegalPlay, "one-turn 11back: b plays under the reversal (K rejected)");
            Pass(e, b, "1turn-pass");
            Check(!e.Field.IsElevenBack && !e.Field.IsElevenBackOneTurn && !e.Field.IsStrengthReversed && !e.Field.IsEmpty,
                "one-turn 11back ends when b passes (the J stays on the field)");
            Check(expired.SequenceEqual(new[] { b }), "one-turn 11back: expiry event for b");
            Check(e.PendingDecision!.PlayerId == c, "one-turn 11back: c's turn");
            Check(e.SubmitPlay(c, C("D4")).Error == CommandError.IllegalPlay, "one-turn 11back: c is back to normal strength (4 rejected)");
            Play(e, c, "CK", "1turn-pass");
        }
        // ---- 11バック「次の手番だけ」：Jの次の人が出したら解除（場はそのまま続く）----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5 S9"));
            e.GetPlayer(a).SetHand(C("HJ C6 C9"));
            e.GetPlayer(b).SetHand(C("H7 C7 DK"));
            e.GetPlayer(c).SetHand(C("D4 CK HK"));
            Play(e, l, "D3", "1turn-play");
            Play(e, a, "HJ", "1turn-play");
            Play(e, b, "H7", "1turn-play"); // 反転中なので 7 は J より強い
            Check(!e.Field.IsElevenBack && !e.Field.IsStrengthReversed && e.Field.CurrentPlay!.Cards[0].Rank == Rank.Seven,
                "one-turn 11back ends right after b plays");
            Check(e.SubmitPlay(c, C("D4")).Error == CommandError.IllegalPlay, "one-turn 11back: 4 rejected against 7 for c");
            Play(e, c, "CK", "1turn-play");
            Check(!e.Field.IsElevenBack, "one-turn 11back stays off");
        }
        // ---- 11バック「次の手番だけ」：誰も続かず場が流れたら解除。新しい親は通常の強さ ----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5"));
            e.GetPlayer(a).SetHand(C("SJ C9"));
            e.GetPlayer(b).SetHand(C("H7 D9"));
            e.GetPlayer(c).SetHand(C("D4 CK"));
            Play(e, l, "D3", "1turn-flush");
            Play(e, a, "SJ", "1turn-flush");
            Pass(e, b, "1turn-flush");
            Pass(e, c, "1turn-flush"); Pass(e, l, "1turn-flush");
            Check(e.Field.IsEmpty && !e.Field.IsElevenBack && !e.Field.IsElevenBackOneTurn && e.PendingDecision!.PlayerId == a,
                "one-turn 11back: flush leaves a clean field");
        }

        // ---- ジョーカーの枚数：1枚なら1枚目（JK0）だけ、2枚なら JK0・JK1 の両方、0枚ならなし ----
        foreach (int count in new[] { 0, 1, 2 })
        {
            var r = RuleSettings.CreateDefault();
            r.Joker.JokerCount = count;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(5));
            e.StartGame();
            var jokers = e.Players.SelectMany(p => p.Hand).Where(x => x.IsJoker).Select(x => x.JokerIndex).OrderBy(i => i).ToArray();
            Check(jokers.SequenceEqual(Enumerable.Range(0, count)), $"joker count {count}: dealt {string.Join(",", jokers)}");
            Check(e.Players.Sum(p => p.HandCount) == 52 + count, $"joker count {count}: deck size");
        }

        // ---- 12ボンバー：ジョーカーも宣言して消せる ----
        {
            var r = RuleSettings.CreateDefault();
            r.Special.TwelveBomberEnabled = true;
            r.Joker.JokerCount = 2;
            var (e, l, a, b, c) = Start(r);
            var discards = new List<CardsDiscardedEvent>();
            IReadOnlyList<Rank>? declared = null;
            e.EventOccurred += (_, ev) =>
            {
                if (ev is CardsDiscardedEvent d) discards.Add(d);
                if (ev is RanksDeclaredEvent rd) declared = rd.Ranks;
            };
            e.GetPlayer(l).SetHand(C("D3 SQ HQ S5"));
            e.GetPlayer(a).SetHand(C("JK0 C6 H5"));
            e.GetPlayer(b).SetHand(C("JK1 C7"));
            e.GetPlayer(c).SetHand(C("D4 CK"));
            Play(e, l, "D3", "bomber-jk");
            Pass(e, a, "bomber-jk"); Pass(e, b, "bomber-jk"); Pass(e, c, "bomber-jk");
            Play(e, l, "SQ HQ", "bomber-jk");
            var d2 = e.PendingDecision!;
            Check(d2.Kind == DecisionKind.DeclareRanks && d2.PlayerId == l && d2.MinCount == 2 && d2.MaxCount == 2, "bomber asks for 2 ranks");
            Check(e.SubmitRankDeclaration(l, new[] { Rank.Joker, Rank.Joker }).Error == CommandError.InvalidSelection, "bomber: duplicate joker rejected");
            Check(e.SubmitRankDeclaration(l, new[] { (Rank)14, Rank.Five }).Error == CommandError.InvalidSelection, "bomber: undefined rank rejected");
            Check(e.SubmitRankDeclaration(l, new[] { Rank.Joker, Rank.Five }).IsAccepted, "bomber: joker declaration accepted");
            Check(declared is not null && declared.Contains(Rank.Joker) && declared.Contains(Rank.Five), "bomber: declared event carries the joker");
            Check(!e.Players.Any(p => p.Hand.Any(x => x.IsJoker)), "bomber: every joker discarded");
            Check(!e.Players.Any(p => p.Hand.Any(x => x.Rank == Rank.Five)), "bomber: declared number discarded");
            Check(e.GetPlayer(a).HandCount == 1 && e.GetPlayer(b).HandCount == 1 && e.GetPlayer(c).HandCount == 2, "bomber: other cards kept");
            Check(discards.Count == 3 && discards.All(x => x.Reason == CardDiscardReason.TwelveBomber)
                && discards.Sum(x => x.Cards.Count(k => k.IsJoker)) == 2, "bomber: joker discards reported");
            // 宣言者は Q・5 を出し切り手札が 0 → あがり
            Check(e.GetPlayer(l).IsFinished, "bomber: declarer finishes when the hand empties");
        }
        {
            // ジョーカーを宣言しなければジョーカーは残る（従来どおり）
            var r = RuleSettings.CreateDefault();
            r.Special.TwelveBomberEnabled = true;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 SQ S5 S9"));
            e.GetPlayer(a).SetHand(C("JK0 C6 H5"));
            e.GetPlayer(b).SetHand(C("C7 C8"));
            e.GetPlayer(c).SetHand(C("D4 CK"));
            Play(e, l, "D3", "bomber-nojk");
            Pass(e, a, "bomber-nojk"); Pass(e, b, "bomber-nojk"); Pass(e, c, "bomber-nojk");
            Play(e, l, "SQ", "bomber-nojk");
            Check(e.SubmitRankDeclaration(l, new[] { Rank.Five }).IsAccepted, "bomber: number only");
            Check(e.GetPlayer(a).Hand.Any(x => x.IsJoker) && e.GetPlayer(a).HandCount == 2, "bomber: joker kept unless declared");
        }
        {
            // 宣言数の上限は14種（13の数字＋ジョーカー）
            Check(GameEngine.MaxDeclarableRanks == 14, "bomber: 14 declarable ranks");
        }

        // ---- 砂嵐の割り込み：ろくろ首（6×2）を砂嵐（3×3）で止める ----
        foreach (var (rank, pair, kind) in new[]
                 {
                     (SpecialEffectType.Rokurokubi, "S6 H6", "rokuro"),
                     (SpecialEffectType.Ambulance, "S9 H9", "ambulance"),
                 })
        {
            RuleSettings Rules()
            {
                var r = RuleSettings.CreateDefault();
                r.Flow.SandstormEnabled = true;
                r.Flow.RokurokubiEnabled = true;
                r.Flow.AmbulanceEnabled = true;
                r.Lock.SuitLockEnabled = false;
                return r;
            }
            void Deal(GameEngine e, int l, int a, int b, int c)
            {
                e.GetPlayer(l).SetHand(C("D3 " + pair + " SK"));
                e.GetPlayer(a).SetHand(C("C5 C8"));               // 砂嵐を持たない → 確認は出るがパス（流す）しかできない
                e.GetPlayer(b).SetHand(C("S3 H3 C3 D7 D8"));      // 砂嵐を持つ
                e.GetPlayer(c).SetHand(C("D4 CK"));
            }

            // (1) 止める：砂嵐で場が流れ、止めた人が親になる。ろくろ首・99車の効果は発動しない
            //     （全員が操作ガイド「初心者用」＝持っていなくても確認を受ける設定）
            {
                var (e, l, a, b, c) = Start(Rules());
                AskAll(e, a, b, c);
                var effects = new List<SpecialEffectType>();
                var flushes = new List<FieldFlushedEvent>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is EffectAppliedEvent ea && ea.Applied) effects.Add(ea.Effect.Type);
                    if (ev is FieldFlushedEvent ff) flushes.Add(ff);
                };
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                flushes.Clear();
                Play(e, l, pair, kind);
                var d = e.PendingDecision!;
                // 「初心者用」なら、砂嵐を持っていない a にも確認が出る
                Check(d.Kind == DecisionKind.SandstormStopChance && d.PlayerId == a && d.CanPass, kind + ": a non-holder is asked too (in turn order)");
                Check(e.GetLegalPlays(a).Count == 0, kind + ": the non-holder can only pass");
                Check(e.SubmitPlay(a, C("C5")).Error == CommandError.IllegalPlay, kind + ": the non-holder cannot play over the window");
                Check(flushes.Count == 0 && e.Field.IsSandstormStopPending, kind + ": nothing flushes before everyone answers");
                Pass(e, a, kind + " non-holder passes");
                d = e.PendingDecision!;
                Check(d.Kind == DecisionKind.SandstormStopChance && d.PlayerId == b && d.CanPass, kind + ": then the holder is asked");
                Check(d.Effect == rank && d.TargetPlayerId == l, kind + ": decision names the interrupted effect and player");
                Check(e.Field.IsSandstormStopPending && !e.Field.IsEmpty && flushes.Count == 0, kind + ": flush is held while the window is open");
                var legal = e.GetLegalPlays(b);
                Check(legal.Count == 1 && legal[0].BeatMethod == BeatMethod.SandstormStop && legal[0].Play!.Count == 3,
                    kind + ": only the 3-card sandstorm is legal over the 2-card field");
                Check(e.GetLegalPlays(a).Count == 0, kind + ": others have no legal plays");
                Check(e.SubmitPlay(b, C("D7 D8")).Error == CommandError.IllegalPlay, kind + ": non-sandstorm rejected during the window");
                Check(e.SubmitPlay(a, C("C5")).Error == CommandError.NotYourTurn, kind + ": not the asked player's turn");
                Play(e, b, "S3 H3 C3", kind + " stop");
                Check(!effects.Contains(rank), kind + ": interrupted effect is cancelled");
                Check(effects.Contains(SpecialEffectType.Sandstorm), kind + ": sandstorm effect applied");
                Check(flushes.Count == 1 && flushes[0].Cause == SpecialEffectType.Sandstorm && flushes[0].NextLeaderId == b,
                    kind + ": sandstorm flushes and the stopper leads");
                Check(e.Field.IsEmpty && !e.Field.IsSandstormStopPending && !e.Field.Lock.IsAnyLocked, kind + ": field clean after the stop");
                Check(e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == b, kind + ": stopper is the new leader");
            }
            // (2) 見送る：ろくろ首・99車が成立して、出した人が親になる（全員「初心者用」）
            {
                var (e, l, a, b, c) = Start(Rules());
                AskAll(e, a, b, c);
                var effects = new List<SpecialEffectType>();
                var passes = new List<PlayerPassedEvent>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is EffectAppliedEvent ea && ea.Applied) effects.Add(ea.Effect.Type);
                    if (ev is PlayerPassedEvent pp) passes.Add(pp);
                };
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                passes.Clear();
                Play(e, l, pair, kind);
                Pass(e, a, kind + " decline");
                Pass(e, b, kind + " decline");
                Check(!effects.Contains(rank) && e.PendingDecision!.PlayerId == c && e.PendingDecision.Kind == DecisionKind.SandstormStopChance,
                    kind + ": the last player is still asked before the flush");
                Pass(e, c, kind + " decline");
                Check(passes.Count == 3 && passes.All(p => p.IsFourStopDecline) && passes.Select(p => p.PlayerId).SequenceEqual(new[] { a, b, c }),
                    kind + ": everyone declines in turn order, none is a normal pass");
                Check(e.GetPlayer(b).PassCountThisRound == 1 && e.GetPlayer(a).PassCountThisRound == 1, kind + ": decline does not count as a pass");
                Check(effects.Contains(rank), kind + ": effect applied after the decline");
                Check(e.Field.IsEmpty && !e.Field.IsSandstormStopPending && e.PendingDecision!.PlayerId == l
                    && e.PendingDecision.Kind == DecisionKind.PlayOrPass, kind + ": original player leads after the decline");
            }
            // (3) 誰も砂嵐を持っていない：「初心者用」なら全員に順に確認し、全員がパス（流す）したら流れる
            {
                var (e, l, a, b, c) = Start(Rules());
                AskAll(e, a, b, c);
                var asked = new List<int>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is DecisionRequestedEvent dr && dr.Decision.Kind == DecisionKind.SandstormStopChance) asked.Add(dr.Decision.PlayerId);
                };
                Deal(e, l, a, b, c);
                e.GetPlayer(b).SetHand(C("S3 H3 D7 D8")); // 3 は2枚だけ
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                Play(e, l, pair, kind);
                Check(asked.SequenceEqual(new[] { a }) && e.Field.IsSandstormStopPending, kind + ": no holder -> still asked, starting with the next player");
                Pass(e, a, kind); Pass(e, b, kind);
                Check(asked.SequenceEqual(new[] { a, b, c }) && e.GetLegalPlays(c).Count == 0, kind + ": every remaining player is asked once");
                Pass(e, c, kind);
                Check(e.Field.IsEmpty && e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == l,
                    kind + ": no holder -> flush after everyone passes");
            }
            // (1') 標準（中級者・上級者用）：砂嵐を持つ b にだけ確認が出る。持たない a・c は聞かれない
            {
                var (e, l, a, b, c) = Start(Rules());
                var asked = new List<int>();
                var flushes = new List<FieldFlushedEvent>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is DecisionRequestedEvent dr && dr.Decision.Kind.IsInterruptChance()) asked.Add(dr.Decision.PlayerId);
                    if (ev is FieldFlushedEvent ff) flushes.Add(ff);
                };
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                flushes.Clear();
                Play(e, l, pair, kind);
                Check(asked.SequenceEqual(new[] { b }) && e.PendingDecision!.Kind == DecisionKind.SandstormStopChance
                    && e.PendingDecision.PlayerId == b && e.Field.IsSandstormStopPending && flushes.Count == 0,
                    kind + " holders-only: only the sandstorm holder is asked");
                Play(e, b, "S3 H3 C3", kind + " holders-only stop");
                Check(flushes.Count == 1 && flushes[0].Cause == SpecialEffectType.Sandstorm && e.PendingDecision!.PlayerId == b,
                    kind + " holders-only: the stop flushes and the stopper leads");
            }
            // (2') 標準：持っている人が見送ると、ほかの人には聞かずに流れる
            {
                var (e, l, a, b, c) = Start(Rules());
                var asked = new List<int>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is DecisionRequestedEvent dr && dr.Decision.Kind.IsInterruptChance()) asked.Add(dr.Decision.PlayerId);
                };
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                Play(e, l, pair, kind);
                Pass(e, b, kind + " holders-only decline");
                Check(asked.SequenceEqual(new[] { b }) && e.Field.IsEmpty && e.PendingDecision!.Kind == DecisionKind.PlayOrPass
                    && e.PendingDecision.PlayerId == l, kind + " holders-only: decline -> flush, the player who flushed leads");
            }
            // (3') 標準：誰も砂嵐を持っていなければ、確認なしですぐに流れる
            {
                var (e, l, a, b, c) = Start(Rules());
                var asked = new List<int>();
                var effects = new List<SpecialEffectType>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is DecisionRequestedEvent dr && dr.Decision.Kind.IsInterruptChance()) asked.Add(dr.Decision.PlayerId);
                    if (ev is EffectAppliedEvent ea && ea.Applied) effects.Add(ea.Effect.Type);
                };
                Deal(e, l, a, b, c);
                e.GetPlayer(b).SetHand(C("S3 H3 D7 D8")); // 3 は2枚だけ
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                Play(e, l, pair, kind);
                Check(asked.Count == 0 && effects.Contains(rank) && e.Field.IsEmpty && !e.Field.IsSandstormStopPending
                    && e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == l,
                    kind + " holders-only: nobody can stop -> no prompt, immediate flush");
            }
            // (3'') 初心者用の人（a）だけは持っていなくても聞かれ、ほかの持っていない人（c）は聞かれない
            {
                var (e, l, a, b, c) = Start(Rules());
                e.AlwaysAskInterruptPlayerIds.Add(a);
                var asked = new List<int>();
                e.EventOccurred += (_, ev) =>
                {
                    if (ev is DecisionRequestedEvent dr && dr.Decision.Kind.IsInterruptChance()) asked.Add(dr.Decision.PlayerId);
                };
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                Play(e, l, pair, kind);
                Check(e.PendingDecision!.PlayerId == a && e.GetLegalPlays(a).Count == 0 && e.PendingDecision.CanPass,
                    kind + " mixed: the beginner without a sandstorm is asked (pass only)");
                Pass(e, a, kind + " mixed");
                Pass(e, b, kind + " mixed");
                Check(asked.SequenceEqual(new[] { a, b }) && e.Field.IsEmpty && e.PendingDecision!.PlayerId == l,
                    kind + " mixed: the non-beginner without a sandstorm is not asked");
            }
            // (4) 砂嵐ルールが無効：3を3枚持っていても確認しない
            {
                var r = Rules();
                r.Flow.SandstormEnabled = false;
                var (e, l, a, b, c) = Start(r);
                Deal(e, l, a, b, c);
                Play(e, l, "D3", kind);
                Pass(e, a, kind); Pass(e, b, kind); Pass(e, c, kind);
                Play(e, l, pair, kind);
                Check(e.Field.IsEmpty && e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == l,
                    kind + ": sandstorm disabled -> immediate flush");
            }
        }
        {
            // 砂嵐を持つ人が2人いれば、進行方向順に1人ずつ確認する。1人目が見送り、2人目が止める
            var r = RuleSettings.CreateDefault();
            r.Flow.SandstormEnabled = true;
            r.Flow.RokurokubiEnabled = true;
            r.Lock.SuitLockEnabled = false;
            r.Joker.JokerTriggersEffects = true;
            r.Joker.JokerCount = 2;
            var (e, l, a, b, c) = Start(r);
            AskAll(e, a, b, c); // 全員「初心者用」：持っていない人にも順に聞く
            e.GetPlayer(l).SetHand(C("D3 S6 H6 SK"));
            e.GetPlayer(a).SetHand(C("S3 H3 JK0 C8"));   // ジョーカー代用の砂嵐（ジョーカー効果発動が有効）
            e.GetPlayer(b).SetHand(C("C5 C9"));
            e.GetPlayer(c).SetHand(C("C3 JK1 D4 CK"));   // 3 が1枚＋ジョーカー1枚 → 砂嵐にならない
            Play(e, l, "D3", "two-holders");
            Pass(e, a, "two-holders"); Pass(e, b, "two-holders"); Pass(e, c, "two-holders");
            Play(e, l, "S6 H6", "two-holders");
            Check(e.PendingDecision!.Kind == DecisionKind.SandstormStopChance && e.PendingDecision.PlayerId == a, "joker-substituted sandstorm may stop");
            Pass(e, a, "two-holders decline");
            Check(e.PendingDecision!.Kind == DecisionKind.SandstormStopChance && e.PendingDecision.PlayerId == b && e.GetLegalPlays(b).Count == 0,
                "two-holders: b (no sandstorm) is asked next");
            Pass(e, b, "two-holders b");
            Check(e.PendingDecision!.PlayerId == c && e.GetLegalPlays(c).Count == 0, "two-holders: c cannot form a sandstorm but is asked");
            Pass(e, c, "two-holders c");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == l, "two-holders: nobody stopped, so the flush stands");
        }

        // ---- 3枚縛り（縛り発生回数2：同じスートが3回続いて縛り）----
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.TriggerCount = 2;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S9"));
            e.GetPlayer(a).SetHand(C("D5 C6"));
            e.GetPlayer(b).SetHand(C("D7 H7"));
            e.GetPlayer(c).SetHand(C("H10 D10 CK"));
            Play(e, l, "D3", "lock3");
            Play(e, a, "D5", "lock3");
            Check(!e.Field.Lock.IsSuitLocked && e.Field.Lock.SuitMatchStreak == 1, "lock3: two in a row does not lock");
            Play(e, b, "D7", "lock3");
            Check(e.Field.Lock.IsSuitLocked && e.Field.Lock.LockedSuits.SequenceEqual(new[] { Suit.Diamond }), "lock3: three in a row locks");
            Check(e.SubmitPlay(c, C("H10")).PlayReject == PlayRejectReason.SuitLockViolation, "lock3: other suit rejected");
            Play(e, c, "D10", "lock3");
        }
        {
            // 既定（発生回数1）：2回続いたら縛り
            var r = RuleSettings.CreateDefault();
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S9"));
            e.GetPlayer(a).SetHand(C("D5 C6"));
            e.GetPlayer(b).SetHand(C("H7 D7"));
            e.GetPlayer(c).SetHand(C("H10 D10 CK"));
            Play(e, l, "D3", "lock2");
            Play(e, a, "D5", "lock2");
            Check(e.Field.Lock.IsSuitLocked, "lock2: two in a row locks by default");
            Check(e.SubmitPlay(b, C("H7")).PlayReject == PlayRejectReason.SuitLockViolation, "lock2: other suit rejected");
        }
        {
            // 3枚縛りでスートが途切れると数え直し
            var r = RuleSettings.CreateDefault();
            r.Lock.TriggerCount = 2;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S9 SA"));
            e.GetPlayer(a).SetHand(C("D5 C6"));
            e.GetPlayer(b).SetHand(C("H7 D9"));
            e.GetPlayer(c).SetHand(C("H10 D10 CK"));
            Play(e, l, "D3", "lock3-break");
            Play(e, a, "D5", "lock3-break");
            Play(e, b, "H7", "lock3-break");
            Check(!e.Field.Lock.IsSuitLocked && e.Field.Lock.SuitMatchStreak == 0, "lock3: streak resets on a different suit");
            Play(e, c, "H10", "lock3-break");
            Check(!e.Field.Lock.IsSuitLocked && e.Field.Lock.SuitMatchStreak == 1, "lock3: streak restarts");
        }

        // ---- 2枚階段 ----
        {
            var r = RuleSettings.CreateDefault();
            r.Sequence.MinLength = 2;
            r.Lock.SuitLockEnabled = false;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 D4 S9"));
            e.GetPlayer(a).SetHand(C("H5 H6 C8"));
            e.GetPlayer(b).SetHand(C("S7 H7 CQ"));
            e.GetPlayer(c).SetHand(C("C10 CJ CK"));
            Play(e, l, "D3 D4", "seq2");
            Check(e.Field.CurrentPlay!.Type == HandType.Sequence && e.Field.CurrentPlay.Count == 2, "seq2: two-card sequence on the field");
            Play(e, a, "H5 H6", "seq2");
            Check(e.SubmitPlay(b, C("S7 H7")).PlayReject == PlayRejectReason.TypeMismatch, "seq2: a pair cannot answer a sequence");
            Pass(e, b, "seq2");
            Play(e, c, "C10 CJ", "seq2");
            Check(e.Field.CurrentPlay!.Type == HandType.Sequence, "seq2: sequence answered by a stronger sequence");
        }
        {
            // 既定（3枚から）では2枚の連番は出せない。Normalize は 2 を保ち、1 以下は 2 へ補正する
            var r = RuleSettings.CreateDefault();
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 D4 S9"));
            Check(e.SubmitPlay(l, C("D3 D4")).PlayReject == PlayRejectReason.InvalidCombination, "seq3: two-card run rejected by default");
            var n = RuleSettings.CreateDefault();
            n.Sequence.MinLength = 2; n.Normalize();
            Check(n.Sequence.MinLength == 2, "normalize keeps MinLength 2");
            n.Sequence.MinLength = 1; n.Normalize();
            Check(n.Sequence.MinLength == 2, "normalize clamps MinLength to 2");
            n.Lock.TriggerCount = 0; n.Normalize();
            Check(n.Lock.TriggerCount == 1, "normalize clamps TriggerCount to 1");
            var round = RuleSettings.FromJson(new RuleSettings { Sequence = { MinLength = 2 }, Lock = { TriggerCount = 2 } }.ToJson());
            Check(round.Sequence.MinLength == 2 && round.Lock.TriggerCount == 2, "seq2 / lock3 survive a JSON round-trip");
        }

        // ---- あがった人のカードが残る（ON）／あがり流し（OFF） ----
        foreach (bool keep in new[] { true, false })
        {
            var r = RuleSettings.CreateDefault();
            r.Lock.SuitLockEnabled = false;
            r.Progress.KeepFieldOnPlayerFinish = keep;
            var (e, l, a, b, c) = Start(r);
            var flushes = new List<FieldFlushedEvent>();
            e.EventOccurred += (_, ev) => { if (ev is FieldFlushedEvent ff) flushes.Add(ff); };
            string label = keep ? "keep-field" : "flush-on-finish";
            e.GetPlayer(l).SetHand(C("D3 S5"));
            e.GetPlayer(a).SetHand(C("H9"));          // 9 を出してあがる
            e.GetPlayer(b).SetHand(C("SK S4"));
            e.GetPlayer(c).SetHand(C("DQ C4"));
            Play(e, l, "D3", label);
            Play(e, a, "H9", label);
            Check(e.GetPlayer(a).IsFinished, label + ": a finished");
            if (keep)
            {
                Check(!e.Field.IsEmpty && e.Field.CurrentPlay!.Cards[0] == Card.Parse("H9") && flushes.Count == 0, "keep: finished player's card stays on the field");
                Check(e.PendingDecision!.PlayerId == b && e.PendingDecision.Kind == DecisionKind.PlayOrPass && e.PendingDecision.CanPass, "keep: next player must beat the card");
                Check(e.SubmitPlay(b, C("S4")).Error == CommandError.IllegalPlay, "keep: weaker card rejected");
                Pass(e, b, label); Pass(e, c, label); Pass(e, l, label);
                Check(e.Field.IsEmpty && flushes.Count == 1 && flushes[0].NextLeaderId == b && e.PendingDecision!.PlayerId == b,
                    "keep: all pass -> the player after the finished one leads");
            }
            else
            {
                Check(e.Field.IsEmpty && flushes.Count == 1 && flushes[0].Cause is null && flushes[0].NextLeaderId == b,
                    "flush: field is flushed the moment a player finishes");
                Check(e.PendingDecision!.PlayerId == b && !e.PendingDecision.CanPass, "flush: the next player leads (cannot pass)");
                Play(e, b, "S4", label); // 9 より弱いカードでも親として出せる
            }
        }
        {
            // あがり流し：8切り・7渡し・10捨てであがっても、次の生存プレイヤーが親
            var r = RuleSettings.CreateDefault();
            r.Progress.KeepFieldOnPlayerFinish = false;
            r.ForbiddenFinish.Enabled = false;
            r.Special.TenDiscardEnabled = true;
            var (e, l, a, b, c) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 S5"));
            e.GetPlayer(a).SetHand(C("H10 C6"));
            e.GetPlayer(b).SetHand(C("SK S4"));
            e.GetPlayer(c).SetHand(C("DQ C4"));
            Play(e, l, "D3", "flush-tendiscard");
            Play(e, a, "H10", "flush-tendiscard");
            Check(e.SubmitCardSelection(a, C("C6")).IsAccepted && e.GetPlayer(a).IsFinished, "flush: finished by 10-discard");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == b, "flush: next player leads after a 10-discard finish");
        }
        {
            // 既定は「残る」（従来どおり）。JSON に項目がない古い設定も「残る」になる
            Check(RuleSettings.CreateDefault().Progress.KeepFieldOnPlayerFinish, "keep-field is the default");
            Check(RuleSettings.FromJson("{\"progress\":{\"roundCount\":3}}").Progress.KeepFieldOnPlayerFinish, "older settings keep the field");
            var off = new RuleSettings { Progress = { KeepFieldOnPlayerFinish = false } };
            Check(!RuleSettings.FromJson(off.ToJson()).Progress.KeepFieldOnPlayerFinish, "flush-on-finish survives a JSON round-trip");
        }
        {
            // 11バックの持続方式：旧設定の strongElevenBackEnabled との互換
            Check(RuleSettings.CreateDefault().ElevenBack.Duration == ElevenBackDuration.UntilFlush, "11back duration default");
            Check(RuleSettings.FromJson("{\"elevenBack\":{\"strongElevenBackEnabled\":true}}").ElevenBack.Duration == ElevenBackDuration.Persistent,
                "old strong flag loads as persistent");
            Check(RuleSettings.FromJson("{\"elevenBack\":{\"strongElevenBackEnabled\":false}}").ElevenBack.Duration == ElevenBackDuration.UntilFlush,
                "old normal flag loads as until-flush");
            var oneTurn = new RuleSettings { ElevenBack = { Duration = ElevenBackDuration.NextTurnOnly } };
            Check(oneTurn.Clone().ElevenBack.Duration == ElevenBackDuration.NextTurnOnly && !oneTurn.ElevenBack.StrongElevenBackEnabled,
                "next-turn-only survives Clone");
            // 項目の順序が逆でも、false の強化フラグは「次の手番だけ」を上書きしない
            Check(RuleSettings.FromJson("{\"elevenBack\":{\"duration\":\"NextTurnOnly\",\"strongElevenBackEnabled\":false}}").ElevenBack.Duration
                == ElevenBackDuration.NextTurnOnly, "duration first, strong=false keeps next-turn-only");
            Check(RuleSettings.FromJson("{\"elevenBack\":{\"strongElevenBackEnabled\":false,\"duration\":\"Persistent\"}}").ElevenBack.StrongElevenBackEnabled,
                "duration wins over a stale strong flag");
            var strong = new RuleSettings { ElevenBack = { StrongElevenBackEnabled = true } };
            Check(strong.Clone().ElevenBack.Duration == ElevenBackDuration.Persistent, "strong flag round-trips as persistent");
            var json = oneTurn.ToJson();
            Check(json.IndexOf("strongElevenBackEnabled", StringComparison.Ordinal) < json.IndexOf("\"duration\"", StringComparison.Ordinal),
                "duration is written after the legacy flag");
            var bad = RuleSettings.CreateDefault();
            bad.ElevenBack.Duration = (ElevenBackDuration)42;
            bad.Normalize();
            Check(bad.ElevenBack.Duration == ElevenBackDuration.UntilFlush, "invalid duration normalized");
        }

        // ---- あがりの順位がイベントに載る ----
        {
            var r = RuleSettings.CreateDefault();
            var (e, l, a, b, c) = Start(r);
            var finishes = new List<PlayerFinishedEvent>();
            e.EventOccurred += (_, ev) => { if (ev is PlayerFinishedEvent pf) finishes.Add(pf); };
            e.GetPlayer(l).SetHand(C("D3"));
            e.GetPlayer(a).SetHand(C("S5"));
            e.GetPlayer(b).SetHand(C("S6"));
            e.GetPlayer(c).SetHand(C("S7"));
            Play(e, l, "D3", "place"); Play(e, a, "S5", "place"); Play(e, b, "S6", "place");
            Check(finishes.Select(f => f.Place).SequenceEqual(new[] { 1, 2, 3, 4 })
                && finishes.Select(f => f.PlayerId).SequenceEqual(new[] { l, a, b, c })
                && finishes[3].Kind == FinishKind.LastRemaining, "finish events carry places 1-4");
        }
        {
            // 反則あがり（2であがる）は最下位から埋まる
            var r = RuleSettings.CreateDefault();
            var (e, l, a, b, c) = Start(r);
            var finishes = new List<PlayerFinishedEvent>();
            e.EventOccurred += (_, ev) => { if (ev is PlayerFinishedEvent pf) finishes.Add(pf); };
            e.GetPlayer(l).SetHand(C("D3 S9"));
            e.GetPlayer(a).SetHand(C("S2"));
            e.GetPlayer(b).SetHand(C("S6 C6"));
            e.GetPlayer(c).SetHand(C("S7 C7"));
            Play(e, l, "D3", "foul-place");
            Play(e, a, "S2", "foul-place");
            Check(finishes.Count == 1 && finishes[0].Kind == FinishKind.Foul && finishes[0].Place == 4, "foul finish takes the last place");
        }
    }

    private static void AttachBot(GameEngine e, Random rnd)
    {
        e.EventOccurred += (_, ev) =>
        {
            if (ev is DecisionRequestedEvent d) Respond(e, d.Decision, rnd);
            if (ev is RoundEndedEvent re && !re.IsGameOver) e.StartNextRound();
        };
    }

    public static (int Pass, int Fail) Run()
    {
        Scripted();
        Scenarios();

        var configs = new List<(string Name, Action<RuleSettings> Apply, int Players)>
        {
            ("default", _ => { }, 4),
            ("all-specials", AllSpecials, 4),
            ("pass-lock+limit", r => { r.Progress.PassLocksUntilFlush = true; r.Progress.PassLimitPerRound = 2; }, 4),
            ("pass-lock+specials", r => { AllSpecials(r); r.Progress.PassLocksUntilFlush = true; r.Progress.PassLimitPerRound = 3; }, 4),
            ("reject-forbidden", r => { AllSpecials(r); r.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.RejectPlay; }, 4),
            ("3p", AllSpecials, 3),
            ("5p-2jk", r => { AllSpecials(r); r.Joker.JokerCount = 2; }, 5),
            ("2p", _ => { }, 2),
            ("no-exchange-noJoker", r => { r.Exchange.Enabled = false; r.Joker.JokerCount = 0; r.Progress.DiamondThreeStarts = false; }, 4),
            ("poor-choose-shuffle", r => { AllSpecials(r); r.Exchange.PoorCanChooseCards = true; r.Exchange.JokerProtected = true; r.Progress.SeatShuffleEachRound = true; r.Exchange.LinkedToMiyakoOchi = false; }, 4),
            ("tenpenchii-easy", r => { r.Exchange.TenpenChiiEnabled = true; r.Exchange.TenpenChiiMaxRank = Rank.Two; }, 4),
            // 2枚階段・3枚縛り（縛り発生回数2）
            ("seq2-lock3", r => { r.Sequence.MinLength = 2; r.Lock.TriggerCount = 2; r.Lock.NumberLockEnabled = true; }, 4),
            ("seq2-lock3-specials", r => { AllSpecials(r); r.Sequence.MinLength = 2; r.Lock.TriggerCount = 2; }, 4),
            // 通常の11バックのみ（強化なし）で全部盛り：場が流れたら必ず解除されること
            ("specials-normal-11back", r => { AllSpecials(r); r.ElevenBack.StrongElevenBackEnabled = false; }, 4),
            // 11バックは次の1手番だけ
            ("one-turn-11back", r => r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly, 4),
            ("one-turn-11back-specials", r => { AllSpecials(r); r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly; }, 4),
            ("one-turn-11back-3p-passlock", r =>
            {
                AllSpecials(r); r.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly; r.Progress.PassLocksUntilFlush = true;
            }, 3),
            // 設定画面から選べるようになった値
            ("jokers-2", r => r.Joker.JokerCount = 2, 4),
            ("jokers-0-specials", r => { AllSpecials(r); r.Joker.JokerCount = 0; }, 4),
            ("pass-limit-3", r => r.Progress.PassLimitPerRound = 3, 4),
            ("pass-limit-3-reject", r =>
            {
                AllSpecials(r); r.Progress.PassLimitPerRound = 3; r.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.RejectPlay;
            }, 4),
            ("leader-daifugo", r => r.Progress.LaterRoundLeader = RoundLeaderRule.Daifugo, 4),
            ("leader-diamond3", r => r.Progress.LaterRoundLeader = RoundLeaderRule.DiamondThreeHolder, 4),
            ("leader-random", r => { AllSpecials(r); r.Progress.LaterRoundLeader = RoundLeaderRule.Random; }, 4),
            ("revolution-3-seq4", r =>
            {
                r.Revolution.MinCards = 3; r.Revolution.SequenceRevolutionEnabled = true; r.Revolution.SequenceRevolutionMinLength = 4;
            }, 4),
            ("exchange-3-0-extra1", r =>
            {
                r.Exchange.DaifugoExchangeCount = 3; r.Exchange.FugoExchangeCount = 0; r.Progress.MaxExtraRounds = 1;
                r.Exchange.TenpenChiiEnabled = true; r.Exchange.TenpenChiiMaxRank = Rank.Jack;
            }, 4),
            // あがり流し（あがった瞬間に場が流れる）
            ("flush-on-finish", r => r.Progress.KeepFieldOnPlayerFinish = false, 4),
            ("flush-on-finish-specials", r => { AllSpecials(r); r.Progress.KeepFieldOnPlayerFinish = false; }, 4),
            ("flush-on-finish-3p", r => { AllSpecials(r); r.Progress.KeepFieldOnPlayerFinish = false; r.Progress.PassLocksUntilFlush = true; }, 3),
            // 砂嵐の割り込みが起きやすい構成（ろくろ首・99車・砂嵐を2枚で成立させる）
            ("sandstorm-stop", r =>
            {
                r.Flow.SandstormEnabled = true; r.Flow.SandstormCount = 2;
                r.Flow.AmbulanceEnabled = true; r.Flow.RokurokubiEnabled = true;
                r.Special.TwelveBomberEnabled = true; r.Joker.JokerCount = 2;
            }, 4),
            // 4止め：8と同じ枚数で止められる設定／標準（2倍）＋ジョーカー2枚・ジョーカー効果あり
            ("fourstop-same", r => { AllSpecials(r); r.Flow.FourStopCount = FourStopCountRule.SameAsEight; }, 4),
            ("fourstop-double-jokers", r => { AllSpecials(r); r.Joker.JokerCount = 2; r.Joker.JokerTriggersEffects = true; }, 4),
            // 得点配分 2・1・-1・-2（合計がマイナスになる・延長戦の判定も通る）
            ("points-minus", r =>
            {
                r.Progress.PointsDaifugo = 2; r.Progress.PointsFugo = 1; r.Progress.PointsHeimin = 0;
                r.Progress.PointsHinmin = -1; r.Progress.PointsDaihinmin = -2;
            }, 4),
            ("points-minus-5p", r =>
            {
                AllSpecials(r);
                r.Progress.PointsDaifugo = 2; r.Progress.PointsFugo = 1; r.Progress.PointsHeimin = 0;
                r.Progress.PointsHinmin = -1; r.Progress.PointsDaihinmin = -2;
            }, 5),
            // エンペラー：場を流さない（標準）／流す＋砂嵐・4止めで止められる（8切り・ろくろ首・99車も砂嵐で止められる）
            ("emperor-no-flush", r => { AllSpecials(r); r.Sequence.EmperorEnabled = true; r.Sequence.EmperorFlushesField = false; }, 4),
            ("emperor-flush-sandstorm", r =>
            {
                AllSpecials(r); r.Sequence.EmperorEnabled = true; r.Sequence.EmperorFlushesField = true; r.Sequence.EmperorCausesRevolution = true;
                r.Flow.SandstormEnabled = true; r.Flow.SandstormCount = 2; r.Flow.FourStopEnabled = true;
                r.Flow.AmbulanceEnabled = true; r.Flow.RokurokubiEnabled = true;
            }, 4),
            ("emperor-flush-sandstorm-3p", r =>
            {
                AllSpecials(r); r.Sequence.EmperorEnabled = true; r.Sequence.EmperorFlushesField = true;
                r.Flow.SandstormEnabled = true; r.Flow.SandstormCount = 2; r.Flow.FourStopEnabled = true;
                r.Flow.FourStopCount = FourStopCountRule.SameAsEight; r.Progress.PassLocksUntilFlush = true;
            }, 3),
        };

        foreach (var (name, apply, count) in configs)
        {
            int ok = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var rules = RuleSettings.CreateDefault();
                apply(rules);
                if (Simulate(rules, count, seed, name))
                {
                    ok++;
                }
            }
            Check(ok == 60, $"simulation {name}: {ok}/60");
        }

        // 設定画面のプリセット（初心者用・中級者用・完全上級者）でも最後まで正しく進む
        foreach (var preset in Enum.GetValues<RulePreset>())
        {
            int ok = 0;
            for (int seed = 0; seed < 30; seed++)
            {
                if (Simulate(RulePresets.Create(preset), 4, 1000 + seed, "preset-" + preset))
                {
                    ok++;
                }
            }
            Check(ok == 30, $"simulation preset {preset}: {ok}/30");
        }
        var keep = RuleSettings.CreateDefault();
        keep.Progress.RoundCount = 7;
        Check(RulePresets.Create(RulePreset.Expert, keep).Progress.RoundCount == 7, "preset keeps round count");
        PresetContents();

        Console.WriteLine("coverage: " + string.Join(", ", Coverage.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        foreach (var must in new[] { "Revolution", "EightCut", "FourStop", "SevenPass", "TenDiscard", "TwelveBomber", "ranks-declared",
                     "FiveSkip", "NineReverse", "SpadeThreeReturn", "SuitLock", "finish:Foul", "finish:MiyakoOchi",
                     "finish:Gekokujo", "transfer:TenpenChii", "transfer:Exchange", "skipped", "extra-round", "deferred",
                     "bomber-joker", "sandstorm-chance", "flush-on-finish", "sandstorm-stop", "eleven-back", "eleven-back-persistent", "eleven-back-one-turn" })
        {
            Check(Coverage.ContainsKey(must), "coverage " + must);
        }
        return (_pass, _fail);
    }

    /// <summary>ルール設定の全カテゴリにある ON/OFF（bool）の項目を、名前つきで列挙する。</summary>
    private static IEnumerable<(string Name, bool Value)> BoolRules(RuleSettings r)
    {
        foreach (var category in typeof(RuleSettings).GetProperties())
        {
            if (category.PropertyType.IsClass && category.PropertyType != typeof(string) && category.GetValue(r) is { } section)
            {
                foreach (var p in category.PropertyType.GetProperties())
                {
                    if (p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite)
                    {
                        yield return ($"{category.Name}.{p.Name}", (bool)p.GetValue(section)!);
                    }
                }
            }
        }
    }

    private static void PresetContents()
    {
        // 設定画面のボタンは CommandParameter に名前を書いて Enum.TryParse で探すため、名前が変わるとボタンが無反応になる
        Check(Enum.GetNames<RulePreset>().SequenceEqual(new[] { "Beginner", "Intermediate", "Expert" }), "preset names match the settings page buttons");
        foreach (var preset in Enum.GetValues<RulePreset>())
        {
            Check(!string.IsNullOrWhiteSpace(RulePresets.DisplayName(preset)) && RulePresets.DisplayName(preset) != preset.ToString(), $"preset {preset} has a display name");
        }

        // 初心者用：革命・8切り・スート縛り・階段・カード交換だけ
        var b = RulePresets.Create(RulePreset.Beginner);
        Check(b.Revolution.Enabled && b.Flow.EightCutEnabled && b.Lock.SuitLockEnabled && b.Sequence.Enabled && b.Exchange.Enabled,
            "beginner: the basic rules are on");
        Check(!b.ElevenBack.Enabled && !b.Joker.SpadeThreeReturnEnabled && !b.RankChange.MiyakoOchiEnabled && !b.ForbiddenFinish.Enabled
            && !b.Special.SevenPassEnabled && !b.Special.TenDiscardEnabled && !b.Special.TwelveBomberEnabled && !b.SkipReverse.FiveSkipEnabled
            && !b.Flow.FourStopEnabled && !b.Flow.SandstormEnabled && !b.Lock.NumberLockEnabled && !b.Revolution.CoupEnabled,
            "beginner: special effects are off");

        // 中級者用：定番＋人気の特殊効果
        var m = RulePresets.Create(RulePreset.Intermediate);
        Check(m.ElevenBack.Enabled && m.Joker.SpadeThreeReturnEnabled && m.RankChange.MiyakoOchiEnabled && m.Special.SevenPassEnabled
            && m.Special.TenDiscardEnabled && m.SkipReverse.FiveSkipEnabled && m.ForbiddenFinish.Enabled && m.Lock.SuitLockEnabled,
            "intermediate: popular rules are on");
        Check(!m.Special.TwelveBomberEnabled && !m.Flow.SandstormEnabled && !m.RankChange.GekokujoEnabled, "intermediate: advanced rules stay off");

        // 完全上級者：すべての ON/OFF 項目が有効（項目を増やしたらここで気付けるよう、全項目を列挙して調べる）
        var x = RulePresets.Create(RulePreset.Expert);
        var off = BoolRules(x).Where(t => !t.Value).Select(t => t.Name).ToList();
        Check(off.Count == 0, "expert: every on/off rule is enabled (off: " + string.Join(", ", off) + ")");
        Check(BoolRules(x).Count() >= 55, $"expert: the bool rule scan found {BoolRules(x).Count()} rules");
        Check(x.ElevenBack.Duration == ElevenBackDuration.Persistent && x.Joker.JokerCount == 2, "expert: strong 11-back and two jokers");

        // 既定値（新規インストール時）はプリセットに合わせて変わっていない
        var d = RuleSettings.CreateDefault();
        Check(d.ElevenBack.Enabled && !d.Special.SevenPassEnabled && d.Joker.JokerCount == 1 && d.Flow.FourStopCount == FourStopCountRule.DoubleEight,
            "defaults are unchanged");
    }

    internal static void AllSpecials(RuleSettings r)
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
        r.Progress.RoundCount = 3;
    }

    /// <summary>ランダムボットでゲームを最後まで実行し、不変条件を検証する。</summary>
    private static bool Simulate(RuleSettings rules, int playerCount, int seed, string name)
    {
        var engine = new GameEngine(rules, Players(playerCount), new SystemRandomSource(seed));
        var rnd = new Random(seed * 7919 + 13);
        var outCards = new HashSet<int>();
        int events = 0;
        bool gameEnded = false;
        string? error = null;
        int deckSize = 52 + engine.Rules.Joker.JokerCount;

        void Fail(string msg)
        {
            error ??= msg;
        }

        // 不変条件：パスした人には、場が流れるかカードが出されるまで手番が回ってこない
        // （「パスしたのに自分の手番のまま」にならないこと）
        int? lastPasser = null;
        int? finishedAwaitingFlush = null;
        HashSet<Rank>? declaredRanks = null;
        int? oneTurnHolder = null;

        // 不変条件：割り込み（4止め・砂嵐）の確認は、実際に止められる手を持つ人にだけ出る（初心者用の人はいない）。
        // 砂嵐は最上位の流し役なので、砂嵐への割り込みは起きない
        engine.EventOccurred += (_, e) =>
        {
            if (e is DecisionRequestedEvent { Decision: { } d } && d.Kind.IsInterruptChance())
            {
                Cover("interrupt-" + (d.Effect?.ToString() ?? "none"));
                if (engine.GetLegalPlays(d.PlayerId).Count == 0)
                {
                    Fail($"interrupt chance for P{d.PlayerId} without a stopper ({d.Effect})");
                }
                if (d.Effect is SpecialEffectType.Sandstorm or SpecialEffectType.SpadeThreeReturn || d.Effect is null)
                {
                    Fail($"interrupt window opened for {d.Effect}");
                }
            }
        };

        engine.EventOccurred += (_, e) =>
        {
            if (++events > 500_000)
            {
                throw new Exception("too many events (無限ループの疑い)");
            }
            switch (e)
            {
                case PlayerPassedEvent pp when !pp.IsFourStopDecline:
                    lastPasser = pp.PlayerId;
                    Cover("pass");
                    break;
                case FieldFlushedEvent:
                case CardsPlayedEvent when lastPasser is not null:
                    lastPasser = null;
                    break;
            }
            // 不変条件：通常の11バックは場が流れたら必ず解除される。強化11バックでも持続フラグと一致する
            if (e is FieldFlushedEvent flushed)
            {
                if (!engine.Rules.ElevenBack.StrongElevenBackEnabled && (flushed.Field.IsElevenBack || engine.Field.IsElevenBack))
                {
                    Fail("eleven back survived a flush without the strong rule");
                }
                if (flushed.Field.IsElevenBack != flushed.Field.IsElevenBackPersistent)
                {
                    Fail("eleven back / persistent flag mismatch after flush");
                }
                if (flushed.Field.IsEightCutPending || flushed.Field.IsSandstormStopPending || flushed.Field.Lock.IsAnyLocked)
                {
                    Fail("pending interrupt or lock survived a flush");
                }
            }
            // 不変条件：通常の11バックでは持続フラグが立たない
            if (!engine.Rules.ElevenBack.StrongElevenBackEnabled && engine.Field.IsElevenBackPersistent)
            {
                Fail("persistent eleven back without the strong rule");
            }
            if (engine.Field.IsElevenBack)
            {
                Cover(engine.Field.IsElevenBackPersistent ? "eleven-back-persistent"
                    : engine.Field.IsElevenBackOneTurn ? "eleven-back-one-turn" : "eleven-back");
            }
            // 不変条件：「次の手番だけ」の11バックは、その設定のときだけ立ち、Jの後の1人の手番が終わったら解除される
            if (engine.Field.IsElevenBackOneTurn && engine.Rules.ElevenBack.Duration != ElevenBackDuration.NextTurnOnly)
            {
                Fail("one-turn eleven back without the next-turn-only rule");
            }
            if (engine.Field.IsElevenBackOneTurn && engine.Field.IsElevenBackPersistent)
            {
                Fail("eleven back is both one-turn and persistent");
            }
            switch (e)
            {
                case EffectAppliedEvent { Effect.Type: SpecialEffectType.ElevenBack or SpecialEffectType.StrongElevenBack }:
                    oneTurnHolder = null; // Jが出るたびに「次の1人」を数え直す
                    break;
                case DecisionRequestedEvent { Decision.Kind: DecisionKind.PlayOrPass } dr when engine.Field.IsElevenBackOneTurn:
                    if (oneTurnHolder is null)
                    {
                        oneTurnHolder = dr.Decision.PlayerId;
                    }
                    else if (oneTurnHolder != dr.Decision.PlayerId)
                    {
                        Fail($"one-turn eleven back still on for the next player ({oneTurnHolder} -> {dr.Decision.PlayerId})");
                    }
                    break;
            }
            switch (e)
            {
                case RoundStartedEvent rs:
                    lastPasser = null;
                    outCards.Clear();
                    // 不変条件：新しいラウンドは、11バック・革命・縛り・割り込みが一切残っていない場から始まる
                    var f0 = engine.Field;
                    if (f0.IsElevenBack || f0.IsElevenBackPersistent || f0.IsRevolution || f0.IsRevolutionLocked
                        || f0.Lock.IsAnyLocked || f0.IsEightCutPending || f0.IsSandstormStopPending || !f0.IsEmpty)
                    {
                        Fail("round started with a dirty field: " + f0);
                    }
                    if (rs.IsExtraRound) Cover("extra-round");
                    break;
                case HandsDealtEvent hd:
                    if (hd.HandCounts.Values.Sum() != deckSize) Fail("deal count");
                    break;
                case CardsPlayedEvent cp:
                    if (cp.Judgement.BeatMethod == BeatMethod.SandstormStop) Cover("sandstorm-stop");
                    foreach (var c in cp.Judgement.Play!.Cards)
                    {
                        if (!outCards.Add(c.Id)) Fail("card played twice " + c);
                    }
                    break;
                case RanksDeclaredEvent rd:
                    declaredRanks = new HashSet<Rank>(rd.Ranks);
                    Cover("ranks-declared");
                    break;
                case CardsDiscardedEvent cd:
                    foreach (var c in cd.Cards)
                    {
                        if (!outCards.Add(c.Id)) Fail("card discarded twice " + c);
                    }
                    // 12ボンバーの捨て札は、直前に宣言された数字だけ（全員の画面に「何を宣言したか」を出すため、宣言が先に届く）。
                    // ジョーカーは Rank.Joker が宣言されたときだけ捨てられる
                    if (cd.Reason == CardDiscardReason.TwelveBomber
                        && (declaredRanks is null || cd.Cards.Any(c => !declaredRanks.Contains(c.Rank))))
                    {
                        Fail("bomber discard without matching declaration " + string.Join(" ", cd.Cards));
                    }
                    if (cd.Reason == CardDiscardReason.TwelveBomber && cd.Cards.Any(c => c.IsJoker)) Cover("bomber-joker");
                    break;
                case CardsTransferredEvent ct:
                    Cover("transfer:" + ct.Reason);
                    break;
                case EffectAppliedEvent ea:
                    if (ea.Applied) Cover(ea.Effect.IsRevolutionTrigger ? "Revolution" : ea.Effect.Type.ToString());
                    break;
                case PlayersSkippedEvent:
                    Cover("skipped");
                    break;
                case PlayerFinishedEvent pf:
                    Cover("finish:" + pf.Kind);
                    if (pf.Kind == FinishKind.Normal && !engine.Rules.Progress.KeepFieldOnPlayerFinish) finishedAwaitingFlush = pf.PlayerId;
                    if (pf.Place < 1 || pf.Place > engine.Players.Count) Fail("finish place out of range " + pf.Place);
                    break;
                case CommandRejectedEvent cr:
                    Fail("rejected " + cr.Result);
                    break;
                case DecisionRequestedEvent dr:
                    // あがり流し：あがった人のカードの上に、次の人が「出すかパス」を求められてはいけない
                    if (finishedAwaitingFlush is { } fin && dr.Decision.Kind == DecisionKind.PlayOrPass)
                    {
                        if (engine.Field.LastPlayerId == fin && !engine.Field.IsEmpty)
                        {
                            Fail("flush-on-finish: the finished player's cards are still on the field");
                        }
                        Cover("flush-on-finish");
                        finishedAwaitingFlush = null;
                    }
                    if (dr.Decision.Kind == DecisionKind.SandstormStopChance) Cover("sandstorm-chance");
                    if (dr.Decision.Kind == DecisionKind.PlayOrPass && dr.Decision.PlayerId == lastPasser)
                    {
                        Fail($"turn returned to passer {lastPasser} without flush field={engine.Field}");
                    }
                    var result = Respond(engine, dr.Decision, rnd);
                    if (result.IsDeferred) Cover("deferred");
                    else Fail("expected deferred " + result);
                    break;
                case RoundEndedEvent re:
                    VerifyRound(engine, re, outCards, deckSize, Fail);
                    if (!re.IsGameOver)
                    {
                        engine.StartNextRound();
                    }
                    break;
                case GameEndedEvent ge:
                    gameEnded = true;
                    if (ge.Result.WinnerIds.Count == 0) Fail("no winner");
                    if (ge.Result.Standings.Sum(s => s.TotalScore) != engine.RoundResults.Sum(r => r.Standings.Sum(s => s.Points))) Fail("score sum");
                    break;
            }
        };

        try
        {
            var start = engine.StartGame();
            if (!start.IsAccepted) Fail("start " + start);
        }
        catch (Exception ex)
        {
            Fail("exception: " + ex);
        }

        if (!gameEnded) Fail($"game not ended phase={engine.Phase} pending={engine.PendingDecision}");
        if (engine.Phase != GamePhase.GameEnded) Fail("phase " + engine.Phase);
        if (engine.RoundNumber < engine.Rules.Progress.RoundCount) Fail("rounds " + engine.RoundNumber);

        if (error is not null)
        {
            Console.WriteLine($"  [{name} seed={seed}] {error}");
            return false;
        }
        return true;
    }

    private static void VerifyRound(GameEngine engine, RoundEndedEvent re, HashSet<int> outCards, int deckSize, Action<string> fail)
    {
        // カード保存則：場に出た・捨てた・手札に残ったカードが重複なくデッキと一致
        var all = new HashSet<int>(outCards);
        foreach (var p in engine.Players)
        {
            foreach (var c in p.Hand)
            {
                if (!all.Add(c.Id)) fail("card duplicated in hand " + c);
            }
        }
        if (all.Count != deckSize) fail($"card conservation {all.Count}/{deckSize}");

        var st = re.Result.Standings;
        int n = engine.Players.Count;
        if (st.Count != n) fail("standings count");
        if (st.Select(s => s.PlayerId).Distinct().Count() != n) fail("standings dup");
        if (!st.Select(s => s.Position).SequenceEqual(Enumerable.Range(1, n))) fail("positions");
        if (st[0].Rank != PlayerRank.Daifugo || st[n - 1].Rank != PlayerRank.Daihinmin) fail("ranks");
        if (!re.Result.IsGekokujo && engine.Players.Any(p => !p.IsFinished)) fail("unfinished player");
        if (re.Result.IsGekokujo) Cover("finish:Gekokujo");
        // 反則・都落ちは必ず下位
        int bottomCount = st.Count(s => s.Kind is FinishKind.Foul or FinishKind.MiyakoOchi);
        if (st.Skip(n - bottomCount).Any(s => s.Kind is not (FinishKind.Foul or FinishKind.MiyakoOchi))) fail("foul placement");
    }

    private static CommandResult Respond(GameEngine engine, PendingDecision d, Random rnd)
    {
        var player = engine.GetPlayer(d.PlayerId);
        switch (d.Kind)
        {
            case DecisionKind.PlayOrPass:
            case DecisionKind.FourStopChance:
            case DecisionKind.SandstormStopChance:
            {
                var legal = engine.GetLegalPlays(d.PlayerId);
                if (legal.Count > 0 && (!d.CanPass || rnd.Next(4) != 0))
                {
                    return engine.SubmitPlay(d.PlayerId, legal[rnd.Next(legal.Count)].Play!.Cards);
                }
                if (d.CanPass)
                {
                    return engine.SubmitPass(d.PlayerId);
                }
                throw new Exception($"no legal play and cannot pass: {d} hand={string.Join(" ", player.Hand)} field={engine.Field}");
            }
            case DecisionKind.RevolutionChoice:
                return engine.SubmitRevolutionChoice(d.PlayerId, rnd.Next(2) == 0);
            case DecisionKind.SelectCards:
            {
                int count = d.MinCount + rnd.Next(d.MaxCount - d.MinCount + 1);
                var cards = player.Hand.OrderBy(_ => rnd.Next()).Take(count).ToList();
                return engine.SubmitCardSelection(d.PlayerId, cards);
            }
            case DecisionKind.DeclareRanks:
            {
                // 0 = ジョーカー。13種の数字と合わせて14種から宣言する
                var ranks = Enumerable.Range(0, 14).Select(i => (Rank)i).OrderBy(_ => rnd.Next()).Take(d.MinCount).ToList();
                return engine.SubmitRankDeclaration(d.PlayerId, ranks);
            }
            default:
                throw new Exception("unknown decision");
        }
    }

    /// <summary>手動で操作する筋書きテスト。</summary>
    private static void Scripted()
    {
        var rules = RuleSettings.CreateDefault();
        var engine = new GameEngine(rules, Players(4), new SystemRandomSource(42));
        var events = new List<GameEvent>();
        engine.EventOccurred += (_, e) => events.Add(e);

        Check(engine.SubmitPass(0).Error == CommandError.WrongPhase, "pass before start");
        Check(engine.StartGame().IsAccepted, "start");
        Check(engine.Phase == GamePhase.Playing && engine.RoundNumber == 1, "playing r1");
        Check(engine.Players.Sum(p => p.HandCount) == 53 && engine.Players.All(p => p.HandCount >= 13), "dealt 53");

        var d3 = Card.Parse("D3");
        var d = engine.PendingDecision!;
        int leader = d.PlayerId;
        Check(engine.GetPlayer(leader).Hand.Contains(d3), "leader holds D3");
        Check(!d.CanPass && engine.SubmitPass(leader).Error == CommandError.CannotPassAsLeader, "leader cannot pass");
        int other = (leader + 1) % 4;
        Check(engine.SubmitPlay(other, engine.GetPlayer(other).Hand.Take(1)).Error == CommandError.NotYourTurn, "not your turn");
        var nonD3 = engine.GetPlayer(leader).Hand.First(c => c != d3 && !c.IsJoker);
        var bad = engine.SubmitPlay(leader, new[] { nonD3 });
        Check(bad.Error == CommandError.IllegalPlay && bad.PlayReject == PlayRejectReason.MissingRequiredCard, "D3 required");
        Check(engine.SubmitPlay(leader, new[] { engine.GetPlayer(other).Hand[0] }).Error == CommandError.CardsNotInHand, "not in hand");

        Check(engine.SubmitPlay(leader, new[] { d3 }).IsAccepted, "play D3");
        Check(engine.Field.CurrentPlay!.Cards[0] == d3, "field D3");
        for (int i = 0; i < 3; i++)
        {
            int p = engine.PendingDecision!.PlayerId;
            Check(p != leader, "turn moved");
            Check(engine.SubmitPass(p).IsAccepted, "pass " + i);
        }
        Check(engine.Field.IsEmpty, "flushed after all pass");
        Check(engine.PendingDecision!.PlayerId == leader && engine.PendingDecision.Kind == DecisionKind.PlayOrPass, "leader again");
        Check(events.OfType<FieldFlushedEvent>().Count() == 1, "flush event");
        Check(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(0, events.Count).Select(i => (long)i)), "event sequence");

        // 時計回りの順番確認
        var order = engine.SeatOrder;
        var played = events.OfType<PlayerPassedEvent>().Select(e => e.PlayerId).ToList();
        int li = order.ToList().IndexOf(leader);
        Check(played.SequenceEqual(new[] { order[(li + 1) % 4], order[(li + 2) % 4], order[(li + 3) % 4] }), "clockwise order");

        // NPC引き継ぎで判断要求が再発行される
        int before = events.OfType<DecisionRequestedEvent>().Count();
        Check(engine.SetPlayerKind(leader, PlayerKind.Remote).IsAccepted, "set kind");
        Check(events.OfType<DecisionRequestedEvent>().Count() == before + 1, "reissue on takeover");

        // 階級
        Check(GameEngine.RankForPosition(2, 4) == PlayerRank.Fugo && GameEngine.RankForPosition(3, 4) == PlayerRank.Hinmin, "rank 4p");
        Check(GameEngine.RankForPosition(2, 3) == PlayerRank.Heimin, "rank 3p");
        Check(GameEngine.RankForPosition(3, 5) == PlayerRank.Heimin && GameEngine.RankForPosition(4, 5) == PlayerRank.Hinmin, "rank 5p");

        // ルールは開始時に複製される（外部での変更が進行中のゲームに影響しない）
        rules.Flow.EightCutEnabled = false;
        Check(engine.Rules.Flow.EightCutEnabled, "rules snapshot");
    }
}
