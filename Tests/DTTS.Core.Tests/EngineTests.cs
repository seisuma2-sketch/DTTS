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

        // ---- 4止め ----
        {
            var r = RuleSettings.CreateDefault();
            r.Flow.FourStopEnabled = true;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(3));
            e.StartGame();
            int l = e.PendingDecision!.PlayerId;
            int a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
            e.GetPlayer(l).SetHand(C("D3 H8 S9"));
            e.GetPlayer(a).SetHand(C("S4 C5"));
            e.GetPlayer(b).SetHand(C("H6 C6"));
            e.GetPlayer(c).SetHand(C("D7 CK"));
            Play(e, l, "D3", "4stop");
            Pass(e, a, "4stop"); Pass(e, b, "4stop"); Pass(e, c, "4stop");
            Play(e, l, "H8", "4stop");
            Check(e.PendingDecision!.Kind == DecisionKind.FourStopChance && e.PendingDecision.PlayerId == a, "4stop chance to holder only");
            Play(e, a, "S4", "4stop");
            Check(e.Field.CurrentPlay!.Rank == Rank.Four && !e.Field.IsEightCutPending, "4 stays on field");
            Check(e.PendingDecision!.Kind == DecisionKind.PlayOrPass && e.PendingDecision.PlayerId == b, "turn continues after stopper");
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
            e.GetPlayer(a).SetHand(C("S4 C5"));
            e.GetPlayer((l + 2) % 4).SetHand(C("H6 C6"));
            e.GetPlayer((l + 3) % 4).SetHand(C("D7 CK"));
            Play(e, l, "D3", "8cut");
            Pass(e, a, "8cut"); Pass(e, (l + 2) % 4, "8cut"); Pass(e, (l + 3) % 4, "8cut");
            Play(e, l, "H8", "8cut");
            Pass(e, a, "8cut decline");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == l, "8cut flush, cutter leads");
        }

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

        // 設定画面のプリセット（標準・シンプル・全部盛り）でも最後まで正しく進む
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
        Check(RulePresets.Create(RulePreset.Chaos, keep).Progress.RoundCount == 7, "preset keeps round count");
        Check(RulePresets.Create(RulePreset.Chaos).Special.TwelveBomberEnabled && !RulePresets.Create(RulePreset.Simple).Lock.SuitLockEnabled,
            "preset contents");

        Console.WriteLine("coverage: " + string.Join(", ", Coverage.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        foreach (var must in new[] { "Revolution", "EightCut", "FourStop", "SevenPass", "TenDiscard", "TwelveBomber", "ranks-declared",
                     "FiveSkip", "NineReverse", "SpadeThreeReturn", "SuitLock", "finish:Foul", "finish:MiyakoOchi",
                     "finish:Gekokujo", "transfer:TenpenChii", "transfer:Exchange", "skipped", "extra-round", "deferred" })
        {
            Check(Coverage.ContainsKey(must), "coverage " + must);
        }
        return (_pass, _fail);
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
        HashSet<Rank>? declaredRanks = null;

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
            switch (e)
            {
                case RoundStartedEvent rs:
                    lastPasser = null;
                    outCards.Clear();
                    if (rs.IsExtraRound) Cover("extra-round");
                    break;
                case HandsDealtEvent hd:
                    if (hd.HandCounts.Values.Sum() != deckSize) Fail("deal count");
                    break;
                case CardsPlayedEvent cp:
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
                    // 12ボンバーの捨て札は、直前に宣言された数字だけ（全員の画面に「何を宣言したか」を出すため、宣言が先に届く）
                    if (cd.Reason == CardDiscardReason.TwelveBomber
                        && (declaredRanks is null || cd.Cards.Any(c => c.IsJoker || !declaredRanks.Contains(c.Rank))))
                    {
                        Fail("bomber discard without matching declaration " + string.Join(" ", cd.Cards));
                    }
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
                    break;
                case CommandRejectedEvent cr:
                    Fail("rejected " + cr.Result);
                    break;
                case DecisionRequestedEvent dr:
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
                var ranks = Enumerable.Range(1, 13).Select(i => (Rank)i).OrderBy(_ => rnd.Next()).Take(d.MinCount).ToList();
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
