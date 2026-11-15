using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

/// <summary>
/// 手番の回り順（画面の席配置との一致・席替え・リバース）、5スキップの人数、砂嵐の確認の待ち時間のテスト。
/// </summary>
internal static class TurnOrderTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(turn): " + name); }
    }

    private static IReadOnlyList<Card> C(string s) => Card.ParseMany(s);

    private static List<PlayerSetup> Players(int n, PlayerKind others = PlayerKind.Npc)
        => Enumerable.Range(0, n).Select(i => new PlayerSetup("P" + i, i == 0 ? PlayerKind.Human : others)).ToList();

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
        SeatLayoutMapping();
        ClockwiseMatchesScreen();
        ReverseMatchesScreen();
        FiveSkip();
        InterruptDelay();
        return (_pass, _fail);
    }

    // ------------------------------------------------------------------
    // 席の配置（自分が下、座席順で次の人が左・向かいが上・その次が右）
    // ------------------------------------------------------------------

    private static void SeatLayoutMapping()
    {
        int[] identity = { 0, 1, 2, 3 };
        Check(Enumerable.Range(0, 4).Select(id => SeatLayout.SlotOf(identity, 0, id)).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "layout: identity order -> 0 bottom, 1 left, 2 top, 3 right");
        Check(SeatLayout.Left == 1 && SeatLayout.Top == 2 && SeatLayout.Right == 3 && SeatLayout.Bottom == 0, "layout: slot constants");

        int[] shuffled = { 2, 0, 3, 1 };
        Check(SeatLayout.SlotOf(shuffled, 0, 0) == SeatLayout.Bottom
            && SeatLayout.SlotOf(shuffled, 0, 3) == SeatLayout.Left
            && SeatLayout.SlotOf(shuffled, 0, 1) == SeatLayout.Top
            && SeatLayout.SlotOf(shuffled, 0, 2) == SeatLayout.Right, "layout: shuffled order follows the seat order from me");
        Check(Enumerable.Range(0, 4).All(slot => SeatLayout.SlotOf(shuffled, 0, SeatLayout.PlayerAt(shuffled, 0, slot)) == slot),
            "layout: PlayerAt is the inverse of SlotOf");
        // ゲスト（自分が席 2）でも自分が下
        Check(SeatLayout.SlotOf(identity, 2, 2) == SeatLayout.Bottom && SeatLayout.PlayerAt(identity, 2, SeatLayout.Left) == 3
            && SeatLayout.PlayerAt(identity, 2, SeatLayout.Right) == 1, "layout: guest viewer sits at the bottom");
        Check(SeatLayout.SlotOf(identity, 0, 9) == -1 && SeatLayout.SlotOf(identity, 9, 0) == -1 && SeatLayout.PlayerAt(identity, 9, 1) == -1,
            "layout: unknown ids");
    }

    /// <summary>
    /// 時計回りのとき、実際の手番の順をそのまま画面の席に写すと 下 → 左 → 上 → 右 → 下 になる（席替えありでも）。
    /// </summary>
    private static void ClockwiseMatchesScreen()
    {
        int shuffledSeeds = 0;
        int ok = 0;
        const int Seeds = 40;
        for (int seed = 0; seed < Seeds; seed++)
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.SeatShuffleEachRound = true;
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.Enabled = false;
            r.Exchange.Enabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(seed));
            var turns = new List<int>();
            e.EventOccurred += (_, ev) =>
            {
                if (ev is DecisionRequestedEvent dr && dr.Decision.Kind == DecisionKind.PlayOrPass) turns.Add(dr.Decision.PlayerId);
            };
            e.StartGame();
            var order = e.SeatOrder.ToArray();
            if (!order.SequenceEqual(new[] { 0, 1, 2, 3 })) shuffledSeeds++;

            int l = e.PendingDecision!.PlayerId;
            int idx = Array.IndexOf(order, l);
            int[] p = Enumerable.Range(0, 4).Select(k => order[(idx + k) % 4]).ToArray();
            e.GetPlayer(p[0]).SetHand(C("D3 C9"));
            e.GetPlayer(p[1]).SetHand(C("S4 D10"));
            e.GetPlayer(p[2]).SetHand(C("S6 DJ"));
            e.GetPlayer(p[3]).SetHand(C("S7 DQ"));
            string label = $"clockwise seed={seed} order={string.Join("", order)}";
            Play(e, p[0], "D3", label);
            Play(e, p[1], "S4", label);
            Play(e, p[2], "S6", label);
            Play(e, p[3], "S7", label);

            var slots = turns.Select(id => SeatLayout.SlotOf(order, 0, id)).ToList();
            bool stepsClockwise = slots.Count == 5 && slots.Zip(slots.Skip(1), (a, b) => ((b - a) % 4 + 4) % 4).All(d => d == 1);
            bool humanBottom = SeatLayout.SlotOf(order, 0, 0) == SeatLayout.Bottom;
            // 自分の次に手番が来る人は、必ず画面の左に座っている
            int afterHuman = order[(Array.IndexOf(order, 0) + 1) % 4];
            bool nextIsLeft = SeatLayout.SlotOf(order, 0, afterHuman) == SeatLayout.Left;
            if (stepsClockwise && humanBottom && nextIsLeft) ok++;
            else Check(false, $"{label}: slots {string.Join(">", slots)} must go bottom>left>top>right");
        }
        Check(ok == Seeds, $"clockwise: turn order matches the screen in {ok}/{Seeds} seeded games");
        Check(shuffledSeeds >= Seeds / 2, $"clockwise: seat shuffle actually changed the order ({shuffledSeeds}/{Seeds})");

        // 席替えのあるゲームを最後まで進めても、毎ラウンドの座席順（RoundStartedEvent）とエンジンの座席順が一致する
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.SeatShuffleEachRound = true;
            r.Progress.RoundCount = 6;
            var e = new GameEngine(r, Players(4, PlayerKind.Npc), new SystemRandomSource(77));
            using var npc = new NpcController(e);
            for (int i = 0; i < 4; i++) npc.Assign(i, NpcProfile.Default, new SystemRandomSource(i + 5));
            e.SetPlayerKind(0, PlayerKind.Npc);
            var orders = new List<int[]>();
            bool consistent = true;
            e.EventOccurred += (_, ev) =>
            {
                if (ev is RoundStartedEvent rs)
                {
                    orders.Add(rs.SeatOrder.ToArray());
                    consistent &= rs.SeatOrder.SequenceEqual(e.SeatOrder);
                }
                if (ev is RoundEndedEvent re && !re.IsGameOver) e.StartNextRound();
            };
            e.StartGame();
            Check(e.Phase == GamePhase.GameEnded && orders.Count >= 6 && consistent, "shuffle: every round reports the engine's seat order");
            Check(orders.Select(o => string.Join(",", o)).Distinct().Count() >= 2, "shuffle: the seat order changes between rounds");
        }
    }

    /// <summary>リバース（9リバース）中は、画面上で 下 → 右 → 上 → 左 と逆回りになる。</summary>
    private static void ReverseMatchesScreen()
    {
        int ok = 0;
        const int Seeds = 30;
        for (int seed = 0; seed < Seeds; seed++)
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.SeatShuffleEachRound = true;
            r.SkipReverse.NineReverseEnabled = true;
            r.Lock.SuitLockEnabled = false;
            r.ElevenBack.Enabled = false;
            r.Exchange.Enabled = false;
            var e = new GameEngine(r, Players(4), new SystemRandomSource(500 + seed));
            var turns = new List<int>();
            e.EventOccurred += (_, ev) =>
            {
                if (ev is DecisionRequestedEvent dr && dr.Decision.Kind == DecisionKind.PlayOrPass) turns.Add(dr.Decision.PlayerId);
            };
            e.StartGame();
            var order = e.SeatOrder.ToArray();
            int l = e.PendingDecision!.PlayerId;
            int idx = Array.IndexOf(order, l);
            int[] p = Enumerable.Range(0, 4).Select(k => order[(idx + k) % 4]).ToArray();
            e.GetPlayer(p[0]).SetHand(C("D3 C10 HK"));
            e.GetPlayer(p[1]).SetHand(C("S9 D4"));
            e.GetPlayer(p[2]).SetHand(C("DQ D6"));
            e.GetPlayer(p[3]).SetHand(C("DJ D5"));
            string label = $"reverse seed={seed}";
            Play(e, p[0], "D3", label);
            Play(e, p[1], "S9", label);   // 9リバース：ここから逆回り
            bool ccw = e.Field.Direction == PlayDirection.CounterClockwise;
            Play(e, p[0], "C10", label);
            Play(e, p[3], "DJ", label);

            var slots = turns.Select(id => SeatLayout.SlotOf(order, 0, id)).ToList();
            var steps = slots.Zip(slots.Skip(1), (a, b) => ((b - a) % 4 + 4) % 4).ToList();
            // p0 → p1（時計回り +1）、9リバース後は p1 → p0 → p3 → p2（すべて -1 ＝ 3）
            if (ccw && turns.SequenceEqual(new[] { p[0], p[1], p[0], p[3], p[2] }) && steps.SequenceEqual(new[] { 1, 3, 3, 3 })) ok++;
            else Check(false, $"{label}: turns {string.Join(",", turns)} steps {string.Join(",", steps)}");
        }
        Check(ok == Seeds, $"reverse: counter-clockwise turns match the screen in {ok}/{Seeds} games");
    }

    // ------------------------------------------------------------------
    // 5スキップ：出した枚数だけ、次に手番が来る人を順に飛ばす
    // ------------------------------------------------------------------

    private static (GameEngine E, int L, int A, int B, int C, List<PlayersSkippedEvent> Skips) Start(RuleSettings r, int players = 4, int seed = 21)
    {
        r.Exchange.Enabled = false;
        r.Lock.SuitLockEnabled = false;
        r.ElevenBack.Enabled = false;
        r.SkipReverse.FiveSkipEnabled = true;
        var e = new GameEngine(r, Players(players), new SystemRandomSource(seed));
        var skips = new List<PlayersSkippedEvent>();
        e.EventOccurred += (_, ev) => { if (ev is PlayersSkippedEvent ps) skips.Add(ps); };
        e.StartGame();
        Check(e.SeatOrder.SequenceEqual(Enumerable.Range(0, players)), "skip: no seat shuffle in these scenarios");
        int l = e.PendingDecision!.PlayerId;
        return (e, l, (l + 1) % players, (l + 2) % players, (l + 3) % players, skips);
    }

    private static void FiveSkip()
    {
        // 5×1（4人）：次の1人を飛ばす
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 C9"));
            e.GetPlayer(a).SetHand(C("S5 C10"));
            e.GetPlayer(b).SetHand(C("H6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 CQ"));
            Play(e, l, "D3", "skip1");
            Play(e, a, "S5", "skip1");
            Check(skips.Count == 1 && skips[0].ByPlayerId == a && skips[0].SkippedPlayerIds.SequenceEqual(new[] { b }), "skip1: exactly the next player is skipped");
            Check(e.PendingDecision!.PlayerId == c && !e.Field.IsEmpty, "skip1: the player after the skipped one plays");
        }
        // 5×2（4人）：次の2人を飛ばし、自分の前の人まで回る
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 C3 C9"));
            e.GetPlayer(a).SetHand(C("S5 H5 C10"));
            e.GetPlayer(b).SetHand(C("H6 D6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 D7 CQ"));
            Play(e, l, "D3 C3", "skip2");
            Play(e, a, "S5 H5", "skip2");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { b, c }), "skip2: two players skipped in turn order");
            Check(e.PendingDecision!.PlayerId == l && e.PendingDecision.Kind == DecisionKind.PlayOrPass && !e.Field.IsEmpty,
                "skip2: the third player (l) must answer the 5-pair");
        }
        // 5×3（4人）：他の全員を飛ばす → 一周して場が流れ、出した人が親
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 C3 H3 C9"));
            e.GetPlayer(a).SetHand(C("S5 H5 C5 C10"));
            e.GetPlayer(b).SetHand(C("H6 D6 S6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 D7 S7 CQ"));
            Play(e, l, "D3 C3 H3", "skip3");
            Play(e, a, "S5 H5 C5", "skip3");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { b, c, l }), "skip3: everyone else skipped once");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == a && e.PendingDecision.Kind == DecisionKind.PlayOrPass,
                "skip3: all skipped -> field flushes and the skipper leads");
        }
        // 5×4（4人）：人数より多く飛ばしても、各人1回ずつで自分が親（4枚出しの革命はこの場面では切っておく）
        {
            var noRevolution = RuleSettings.CreateDefault();
            noRevolution.Revolution.Enabled = false;
            var (e, l, a, b, c, skips) = Start(noRevolution);
            e.GetPlayer(l).SetHand(C("D3 C3 H3 S3 C9"));
            e.GetPlayer(a).SetHand(C("S5 H5 C5 D5 C10"));
            e.GetPlayer(b).SetHand(C("H6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 CQ"));
            Play(e, l, "D3 C3 H3 S3", "skip4");
            Play(e, a, "S5 H5 C5 D5", "skip4");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.Count == 3 && skips[0].SkippedPlayerIds.Distinct().Count() == 3,
                "skip4: more skips than players skips each player once");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == a, "skip4: skipper leads");
        }
        // 3人で 5×2：残りの2人を飛ばして自分が親
        {
            var (e, l, a, b, _, skips) = Start(RuleSettings.CreateDefault(), players: 3);
            e.GetPlayer(l).SetHand(C("D3 C3 C9"));
            e.GetPlayer(a).SetHand(C("S5 H5 C10"));
            e.GetPlayer(b).SetHand(C("H6 D6 CJ"));
            Play(e, l, "D3 C3", "skip-3p");
            Play(e, a, "S5 H5", "skip-3p");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { b, l }), "skip-3p: both others skipped");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == a, "skip-3p: skipper leads");
        }
        // あがった人は数えない：b があがった後の 5×1 は a を飛ばして c へ
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 S5 C9"));
            e.GetPlayer(a).SetHand(C("H4 D10"));
            e.GetPlayer(b).SetHand(C("SK"));
            e.GetPlayer(c).SetHand(C("C4 CJ"));
            Play(e, l, "D3", "skip-finished");
            Pass(e, a, "skip-finished");
            Play(e, b, "SK", "skip-finished");   // b はあがり（場には残る）
            Pass(e, c, "skip-finished"); Pass(e, l, "skip-finished"); Pass(e, a, "skip-finished");
            Check(e.GetPlayer(b).IsFinished && e.Field.IsEmpty && e.PendingDecision!.PlayerId == c, "skip-finished: c leads after b finished");
            Play(e, c, "C4", "skip-finished");
            Play(e, l, "S5", "skip-finished");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { a }), "skip-finished: the finished player is not counted");
            Check(e.PendingDecision!.PlayerId == c, "skip-finished: c plays next");
        }
        // 反時計回り（9リバース中）：飛ばすのも逆回りの次の人
        {
            var r = RuleSettings.CreateDefault();
            r.SkipReverse.NineReverseEnabled = true;
            var (e, l, a, b, c, skips) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 C10"));
            e.GetPlayer(a).SetHand(C("S9 S5 D8"));
            e.GetPlayer(b).SetHand(C("H6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 CQ"));
            Play(e, l, "D3", "skip-ccw");
            Play(e, a, "S9", "skip-ccw");        // 逆回りになり、次は l
            Check(e.PendingDecision!.PlayerId == l, "skip-ccw: reverse goes back to l");
            Pass(e, l, "skip-ccw"); Pass(e, c, "skip-ccw"); Pass(e, b, "skip-ccw");
            Check(e.Field.IsEmpty && e.PendingDecision!.PlayerId == a && e.Field.Direction == PlayDirection.CounterClockwise,
                "skip-ccw: a leads, still counter-clockwise");
            Play(e, a, "S5", "skip-ccw");
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { l }), "skip-ccw: skips the counter-clockwise neighbour");
            Check(e.PendingDecision!.PlayerId == c, "skip-ccw: then c plays");
        }
        // パス固定：場が流れるまで抜けている人は数えず、次に手番が来る人を飛ばす
        {
            var r = RuleSettings.CreateDefault();
            r.Progress.PassLocksUntilFlush = true;
            var (e, l, a, b, c, skips) = Start(r);
            e.GetPlayer(l).SetHand(C("D3 C9"));
            e.GetPlayer(a).SetHand(C("H6 CJ"));
            e.GetPlayer(b).SetHand(C("H4 CQ"));
            e.GetPlayer(c).SetHand(C("S5 CK"));
            Play(e, l, "D3", "skip-locked");
            Pass(e, a, "skip-locked");           // a は場が流れるまで出せない
            Play(e, b, "H4", "skip-locked");
            Play(e, c, "S5", "skip-locked");     // 1人飛ばし：l を飛ばし、抜けている a も飛んで b
            Check(skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { l }), "skip-locked: the locked-out player is not counted");
            Check(e.PendingDecision!.PlayerId == b, "skip-locked: b plays next");
        }
        // 5であがった（場に残る設定）：次の人を飛ばして、その次へ
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 C9"));
            e.GetPlayer(a).SetHand(C("S5"));
            e.GetPlayer(b).SetHand(C("H6 CJ"));
            e.GetPlayer(c).SetHand(C("H7 CQ"));
            Play(e, l, "D3", "skip-finish");
            Play(e, a, "S5", "skip-finish");
            Check(e.GetPlayer(a).IsFinished && skips.Count == 1 && skips[0].SkippedPlayerIds.SequenceEqual(new[] { b }), "skip-finish: skip applies after finishing");
            Check(e.PendingDecision!.PlayerId == c && !e.Field.IsEmpty, "skip-finish: c answers the 5");
        }
        // 飛ばされた人も、次に誰かが出せば再び出せる（一時的なパス扱い）
        {
            var (e, l, a, b, c, skips) = Start(RuleSettings.CreateDefault());
            e.GetPlayer(l).SetHand(C("D3 C9 CK"));
            e.GetPlayer(a).SetHand(C("S5 C10"));
            e.GetPlayer(b).SetHand(C("H6 CJ HQ"));
            e.GetPlayer(c).SetHand(C("H7 CQ"));
            Play(e, l, "D3", "skip-again");
            Play(e, a, "S5", "skip-again");
            Play(e, c, "H7", "skip-again");
            Check(e.PendingDecision!.PlayerId == l, "skip-again: after c, l");
            Play(e, l, "C9", "skip-again");
            Check(e.PendingDecision!.PlayerId == a, "skip-again: then a");
            Pass(e, a, "skip-again");
            Check(e.PendingDecision!.PlayerId == b, "skip-again: the skipped player gets a turn again");
        }
    }

    // ------------------------------------------------------------------
    // 砂嵐の確認：NPC は持っていてもいなくても同じ短い待ち時間で答える
    // ------------------------------------------------------------------

    private sealed class RecordingScheduler : INpcScheduler
    {
        public readonly List<(TimeSpan Delay, Action Action)> Pending = new();

        public void Schedule(TimeSpan delay, Action action) => Pending.Add((delay, action));

        public void RunAll()
        {
            while (Pending.Count > 0)
            {
                var next = Pending[0];
                Pending.RemoveAt(0);
                next.Action();
            }
        }
    }

    private static void InterruptDelay()
    {
        var r = RuleSettings.CreateDefault();
        r.Exchange.Enabled = false;
        r.Lock.SuitLockEnabled = false;
        r.Flow.SandstormEnabled = true;
        r.Flow.RokurokubiEnabled = true;
        var e = new GameEngine(r, Players(4, PlayerKind.Human), new SystemRandomSource(31));
        e.StartGame();
        int l = e.PendingDecision!.PlayerId;
        int a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
        e.GetPlayer(l).SetHand(C("D3 S6 H6 SK"));
        e.GetPlayer(a).SetHand(C("C5 C8"));
        e.GetPlayer(b).SetHand(C("S3 H3 C3 D7"));   // 砂嵐を持つ
        e.GetPlayer(c).SetHand(C("D4 CK"));
        Play(e, l, "D3", "delay");
        Pass(e, a, "delay"); Pass(e, b, "delay"); Pass(e, c, "delay");

        var scheduler = new RecordingScheduler();
        var timing = new NpcTimingOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1000),
            Jitter = TimeSpan.FromMilliseconds(400),
            SideDecisionDelay = TimeSpan.FromMilliseconds(60),
        };
        using var npc = new NpcController(e, scheduler, timing, new SystemRandomSource(3));
        var asked = new List<int>();
        e.EventOccurred += (_, ev) =>
        {
            if (ev is DecisionRequestedEvent dr && dr.Decision.Kind == DecisionKind.SandstormStopChance) asked.Add(dr.Decision.PlayerId);
        };
        foreach (int id in new[] { a, b, c })
        {
            e.SetPlayerKind(id, PlayerKind.Npc);
            npc.Assign(id, NpcProfile.Default, new SystemRandomSource(id));
            // 持っていない人にも聞く設定（操作ガイド「初心者用」相当）で、聞かれたときの待ち時間がそろっているかを見る
            e.AlwaysAskInterruptPlayerIds.Add(id);
        }

        var delays = new List<TimeSpan>();
        Play(e, l, "S6 H6", "delay");          // ろくろ首
        for (int guard = 0; guard < 10 && scheduler.Pending.Count > 0; guard++)
        {
            delays.Add(scheduler.Pending[0].Delay);
            var next = scheduler.Pending[0];
            scheduler.Pending.RemoveAt(0);
            next.Action();
            if (e.PendingDecision is { } d && !d.Kind.IsInterruptChance()) break;
        }
        Check(asked.Count >= 1 && asked[0] == a, "delay: the non-holder NPC is asked first");
        Check(delays.Count == asked.Count && delays.All(x => x == timing.SideDecisionDelay),
            $"delay: every sandstorm answer uses the same short delay ({string.Join(",", delays.Select(x => x.TotalMilliseconds))})");
        Check(e.Field.IsEmpty && e.PendingDecision is not null && !e.PendingDecision.Kind.IsInterruptChance(), "delay: the window resolves (stopped or flushed)");
        scheduler.Pending.Clear();
    }
}
