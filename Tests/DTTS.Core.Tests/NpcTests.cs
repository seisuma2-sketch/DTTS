using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

internal static class NpcTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(npc): " + name); }
    }

    private static IReadOnlyList<Card> C(string s) => Card.ParseMany(s);

    /// <summary>任意の盤面から判断材料を作る。</summary>
    private static NpcContext Ctx(string hand, FieldState field, RuleSettings? rules = null, int minOpponent = 8, bool canPass = true)
    {
        rules ??= RuleSettings.CreateDefault();
        var judge = new JudgeLogic(rules);
        var h = C(hand);
        var decision = new PendingDecision(DecisionKind.PlayOrPass, 0) { CanPass = canPass && !field.IsEmpty };
        return new NpcContext(0, h, field, rules, judge, decision, judge.GetLegalPlays(h, field),
            new Dictionary<int, int> { [1] = minOpponent, [2] = 10, [3] = 10 }, Array.Empty<Card>());
    }

    private static FieldState FieldWith(string cards, RuleSettings? rules = null)
    {
        var judge = new JudgeLogic(rules ?? RuleSettings.CreateDefault());
        var j = judge.Judge(C(cards), FieldState.CreateInitial());
        return FieldState.CreateInitial().WithPlay(9, j.Play!, j.NextLock);
    }

    private static string Decide(NpcProfile profile, NpcContext ctx, int seed = 1)
    {
        var d = new StandardNpcBrain(profile, new SystemRandomSource(seed)).DecidePlay(ctx);
        return d.IsPass ? "PASS" : string.Join(" ", d.Cards!.Select(c => c.Code).OrderBy(s => s));
    }

    public static (int Pass, int Fail) Run()
    {
        UnitDecisions();
        ControllerTakeover();
        Tournaments();
        return (_pass, _fail);
    }

    private static void UnitDecisions()
    {
        var normal = new NpcProfile(NpcDifficulty.Normal, NpcPersonality.Steady);
        var hard = new NpcProfile(NpcDifficulty.Hard, NpcPersonality.Steady);
        var empty = FieldState.CreateInitial();

        // あがれるならあがる
        Check(Decide(hard, Ctx("S5", FieldWith("H4"))) == "S5", "finish");
        // 禁止カードだけを残さない（2を先に出す）
        Check(Decide(hard, Ctx("H2 S4", empty)) == "H2", "avoid forbidden-only remainder");
        // 8切りで親を取り、残り1枚であがる
        Check(Decide(hard, Ctx("H8 S4", FieldWith("H3"))) == "H8", "8cut then finish");
        // 弱いカードから処理
        Check(Decide(hard, Ctx("S4 HK SA C9 D6", FieldWith("H3"))) == "S4", "dump weakest");
        // ペアを崩さない
        Check(Decide(hard, Ctx("S5 H5 D7 C9 SQ", FieldWith("H4"))) == "D7", "keep pair: " + Decide(hard, Ctx("S5 H5 D7 C9 SQ", FieldWith("H4"))));
        // 親ではペアをまとめて出す
        Check(Decide(hard, Ctx("S5 H5 DK CA SQ", empty)) == "H5 S5", "lead pair");
        // 温存はジョーカー・強カードを使わずパス、アグレッシブは取りに行く
        var conserver = new NpcProfile(NpcDifficulty.Hard, NpcPersonality.Conserver);
        var aggressive = new NpcProfile(NpcDifficulty.Hard, NpcPersonality.Aggressive);
        var ctxStrong = Ctx("S4 D6 C9 HJ SK JK0 S2", FieldWith("HQ"));
        Check(Decide(conserver, ctxStrong) == "PASS", "conserver passes: " + Decide(conserver, ctxStrong));
        Check(Decide(aggressive, ctxStrong) != "PASS", "aggressive plays: " + Decide(aggressive, ctxStrong));
        Check(Decide(aggressive, ctxStrong) != "JK0", "aggressive still prefers natural over joker");
        // 相手があがりそうなら温存もやめて止めに行く
        Check(Decide(conserver, Ctx("S4 D6 C9 HJ SK JK0 S2", FieldWith("HQ"), minOpponent: 1)) != "PASS", "conserver blocks near-finish opponent");

        // 革命の評価：弱い手札なら革命狙いは革命を起こす／強い手札なら堅実は起こさない
        var seeker = new NpcProfile(NpcDifficulty.Hard, NpcPersonality.RevolutionSeeker);
        var weakHand = Ctx("S6 H6 D6 C6 S3 H4 D4 C5 S7", empty);
        Check(Decide(seeker, weakHand) == "C6 D6 H6 S6", "seeker revolts with weak hand: " + Decide(seeker, weakHand));
        var strongHand = Ctx("S6 H6 D6 C6 S2 H2 DA CA JK0", empty);
        Check(Decide(hard, strongHand) != "C6 D6 H6 S6", "steady avoids revolution with strong hand: " + Decide(hard, strongHand));
        var revChoice = new NpcContext(0, C("S3 H4 D4 C5 S7"), empty, RuleSettings.CreateDefault(), new JudgeLogic(RuleSettings.CreateDefault()),
            new PendingDecision(DecisionKind.RevolutionChoice, 0), Array.Empty<PlayJudgement>(), new Dictionary<int, int>(), Array.Empty<Card>());
        Check(new StandardNpcBrain(seeker).ChooseRevolution(revChoice), "revolution choice weak hand");
        var revChoiceStrong = new NpcContext(0, C("S2 H2 DA CA JK0"), empty, RuleSettings.CreateDefault(), new JudgeLogic(RuleSettings.CreateDefault()),
            new PendingDecision(DecisionKind.RevolutionChoice, 0), Array.Empty<PlayJudgement>(), new Dictionary<int, int>(), Array.Empty<Card>());
        Check(!new StandardNpcBrain(hard).ChooseRevolution(revChoiceStrong), "no revolution with strong hand");

        // カード選択：弱いカードを渡す
        var give = new NpcContext(0, C("S3 H9 DK S2 JK0 C4"), empty, RuleSettings.CreateDefault(), new JudgeLogic(RuleSettings.CreateDefault()),
            new PendingDecision(DecisionKind.SelectCards, 0) { Purpose = CardSelectionPurpose.ExchangeGive, MinCount = 2, MaxCount = 2 },
            Array.Empty<PlayJudgement>(), new Dictionary<int, int>(), Array.Empty<Card>());
        var given = new StandardNpcBrain(hard).SelectCards(give).Select(c => c.Code).OrderBy(s => s).ToList();
        Check(given.SequenceEqual(new[] { "C4", "S3" }), "give weakest: " + string.Join(",", given));
        // 10捨ては最大枚数
        var discard = new NpcContext(0, C("S3 H9 DK"), empty, RuleSettings.CreateDefault(), new JudgeLogic(RuleSettings.CreateDefault()),
            new PendingDecision(DecisionKind.SelectCards, 0) { Purpose = CardSelectionPurpose.TenDiscard, MinCount = 0, MaxCount = 2 },
            Array.Empty<PlayJudgement>(), new Dictionary<int, int>(), Array.Empty<Card>());
        Check(new StandardNpcBrain(hard).SelectCards(discard).Count == 2, "ten discard max");
        // 12ボンバー：自分が多く持つ弱い数字
        var bomb = new NpcContext(0, C("S4 H4 D4 SK"), empty, RuleSettings.CreateDefault(), new JudgeLogic(RuleSettings.CreateDefault()),
            new PendingDecision(DecisionKind.DeclareRanks, 0) { MinCount = 1, MaxCount = 1 },
            Array.Empty<PlayJudgement>(), new Dictionary<int, int>(), Array.Empty<Card>());
        Check(new StandardNpcBrain(hard).DeclareRanks(bomb).Single() == Rank.Four, "bomber declares own rank");

        // つよい：カード記憶で「返されない手」を見抜く（残りは2が全部出た状態のA）
        var r = RuleSettings.CreateDefault();
        r.Joker.JokerCount = 0;
        var seen = C("S2 H2 D2 C2");
        var judge = new JudgeLogic(r);
        var hand = C("SA S4");
        var field = FieldWith("HK", r);
        var ctxHard = new NpcContext(0, hand, field, r, judge, new PendingDecision(DecisionKind.PlayOrPass, 0) { CanPass = true },
            judge.GetLegalPlays(hand, field), new Dictionary<int, int> { [1] = 8 }, seen);
        Check(Decide(hard, ctxHard) == "SA", "hard knows SA is boss then finishes");
    }

    private sealed class ManualScheduler : INpcScheduler
    {
        public readonly Queue<Action> Pending = new();
        public void Schedule(TimeSpan delay, Action action) => Pending.Enqueue(action);
    }

    /// <summary>遅延スケジューラー（UIタイマー相当）＋人間プレイヤーのNPC引き継ぎ。</summary>
    private static void ControllerTakeover()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var players = new List<PlayerSetup>
            {
                new("You", PlayerKind.Human), new("A", PlayerKind.Npc), new("B", PlayerKind.Npc), new("C", PlayerKind.Npc),
            };
            var rules = RuleSettings.CreateDefault();
            rules.Progress.RoundCount = 2;
            var engine = new GameEngine(rules, players, new SystemRandomSource(seed));
            var scheduler = new ManualScheduler();
            using var npc = new NpcController(engine, scheduler, random: new SystemRandomSource(seed));
            for (int i = 1; i < 4; i++) npc.Assign(i, new NpcProfile((NpcDifficulty)(i % 3), (NpcPersonality)i), new SystemRandomSource(seed + i));
            int failures = 0, rejected = 0;
            npc.StrategyFailed += (_, _) => failures++;
            engine.EventOccurred += (_, e) =>
            {
                if (e is CommandRejectedEvent) rejected++;
                if (e is RoundEndedEvent re && !re.IsGameOver) scheduler.Schedule(TimeSpan.Zero, () => engine.StartNextRound());
            };
            engine.StartGame();

            var humanBot = new StandardNpcBrain(new NpcProfile(NpcDifficulty.Easy, NpcPersonality.Steady), new SystemRandomSource(seed));
            int humanMoves = 0, steps = 0;
            bool tookOver = false;
            while (engine.Phase != GamePhase.GameEnded && steps++ < 100_000)
            {
                if (scheduler.Pending.Count > 0)
                {
                    scheduler.Pending.Dequeue()();
                    continue;
                }
                var d = engine.PendingDecision;
                if (d is null) break;
                if (d.PlayerId == 0 && engine.GetPlayer(0).Kind == PlayerKind.Human)
                {
                    if (++humanMoves == 15 && !tookOver)
                    {
                        // 「切断」：人間の席をNPCに引き継ぐ
                        tookOver = true;
                        npc.Assign(0, NpcProfile.Default);
                        engine.SetPlayerKind(0, PlayerKind.Npc);
                        continue;
                    }
                    var ctx = NpcContext.FromEngine(engine, 0, npc.SeenCards);
                    switch (d.Kind)
                    {
                        case DecisionKind.PlayOrPass:
                        case DecisionKind.FourStopChance:
                            var p = humanBot.DecidePlay(ctx);
                            if (p.IsPass) engine.SubmitPass(0); else engine.SubmitPlay(0, p.Cards!);
                            break;
                        case DecisionKind.SelectCards: engine.SubmitCardSelection(0, humanBot.SelectCards(ctx)); break;
                        case DecisionKind.DeclareRanks: engine.SubmitRankDeclaration(0, humanBot.DeclareRanks(ctx)); break;
                        case DecisionKind.RevolutionChoice: engine.SubmitRevolutionChoice(0, true); break;
                    }
                    continue;
                }
                // NPCの判断待ちなのにスケジュールが空 = 取りこぼし
                Check(false, $"npc decision dropped seed={seed} {d}");
                break;
            }
            Check(engine.Phase == GamePhase.GameEnded, $"takeover game ended seed={seed} phase={engine.Phase}");
            Check(failures == 0 && rejected == 0, $"no npc failures seed={seed} f={failures} r={rejected}");
        }
    }

    private sealed record Stats(double[] Scores, int Games, int Plays, int Passes, int Revolutions, int Failures, int Rejected, long Ms);

    /// <summary>判断時間を計測する戦略ラッパー。</summary>
    private sealed class TimedStrategy : INpcStrategy
    {
        private readonly INpcStrategy _inner;
        public static double MaxMs;
        public static NpcProfile? MaxProfile;
        public TimedStrategy(INpcStrategy inner) => _inner = inner;
        public NpcProfile Profile => _inner.Profile;
        public NpcPlayDecision DecidePlay(NpcContext c)
        {
            var sw = Stopwatch.StartNew();
            var r = _inner.DecidePlay(c);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > MaxMs) { MaxMs = ms; MaxProfile = Profile; }
            return r;
        }
        public IReadOnlyList<Card> SelectCards(NpcContext c) => _inner.SelectCards(c);
        public IReadOnlyList<Rank> DeclareRanks(NpcContext c) => _inner.DeclareRanks(c);
        public bool ChooseRevolution(NpcContext c) => _inner.ChooseRevolution(c);
    }

    private static Stats RunGames(NpcProfile[] profiles, int games, Action<RuleSettings>? configure = null, int seedOffset = 0)
    {
        var scores = new double[4];
        int plays = 0, passes = 0, revolutions = 0, failures = 0, rejected = 0, ended = 0;
        var sw = Stopwatch.StartNew();
        for (int gi = 0; gi < games; gi++)
        {
            int g = gi + seedOffset;
            var rules = RuleSettings.CreateDefault();
            configure?.Invoke(rules);
            // 座席の有利不利を打ち消すため、プロファイルの席を回す
            int shift = g % 4;
            var seats = Enumerable.Range(0, 4).Select(i => profiles[(i + shift) % 4]).ToArray();
            var engine = new GameEngine(rules, seats.Select((p, i) => new PlayerSetup("N" + i, PlayerKind.Npc)).ToList(), new SystemRandomSource(g * 31 + 7));
            using var npc = new NpcController(engine, random: new SystemRandomSource(g));
            for (int i = 0; i < 4; i++) npc.Assign(i, new TimedStrategy(new StandardNpcBrain(seats[i], new SystemRandomSource(g * 100 + i))));
            npc.StrategyFailed += (_, _) => failures++;
            engine.EventOccurred += (_, e) =>
            {
                switch (e)
                {
                    case CardsPlayedEvent: plays++; break;
                    case PlayerPassedEvent pp when !pp.IsFourStopDecline: passes++; break;
                    case EffectAppliedEvent ea when ea.Applied && ea.Effect.IsRevolutionTrigger: revolutions++; break;
                    case CommandRejectedEvent: rejected++; break;
                    case RoundEndedEvent re when !re.IsGameOver: engine.StartNextRound(); break;
                    case GameEndedEvent: ended++; break;
                }
            };
            engine.StartGame();
            for (int i = 0; i < 4; i++)
            {
                scores[(i + shift) % 4] += engine.GetPlayer(i).TotalScore;
            }
        }
        Check(ended == games, $"all games ended {ended}/{games}");
        return new Stats(scores.Select(s => s / games).ToArray(), games, plays, passes, revolutions, failures, rejected, sw.ElapsedMilliseconds);
    }

    private static void Tournaments()
    {
        // 全12プロファイルで不正応答・例外が出ないこと（全特殊ルールON）
        var all = (from d in Enum.GetValues<NpcDifficulty>() from p in Enum.GetValues<NpcPersonality>() select new NpcProfile(d, p)).ToArray();
        var sweeps = new (string Name, Action<RuleSettings> Apply)[]
        {
            ("all-specials+2jk+poorChoose", r => { EngineTests.AllSpecials(r); r.Joker.JokerCount = 2; r.Exchange.PoorCanChooseCards = true; }),
            ("all-specials+passlock+limit", r => { EngineTests.AllSpecials(r); r.Progress.PassLocksUntilFlush = true; r.Progress.PassLimitPerRound = 3; }),
            ("all-specials+reject-forbidden", r => { EngineTests.AllSpecials(r); r.ForbiddenFinish.Penalty = ForbiddenFinishPenalty.RejectPlay; r.Joker.JokerCount = 2; }),
        };
        foreach (var (name, apply) in sweeps)
        {
            for (int i = 0; i < all.Length; i += 4)
            {
                var st = RunGames(all.Skip(i).Take(4).ToArray(), 12, apply);
                Check(st.Failures == 0 && st.Rejected == 0, $"{name} profiles {i}-{i + 3}: failures={st.Failures} rejected={st.Rejected}");
                Console.WriteLine($"  {name} profiles {i}-{i + 3}: {st.Ms}ms / {st.Games} games");
            }
        }
        Console.WriteLine($"  max DecidePlay = {TimedStrategy.MaxMs:F1}ms ({TimedStrategy.MaxProfile?.DisplayName})");

        // 強さの序列：つよい ＞ ふつう ＞ よわい（同じ性格どうし）
        var hard = new NpcProfile(NpcDifficulty.Hard, NpcPersonality.Steady);
        var normal = new NpcProfile(NpcDifficulty.Normal, NpcPersonality.Steady);
        var easy = new NpcProfile(NpcDifficulty.Easy, NpcPersonality.Steady);
        var hve = RunGames(new[] { hard, easy, easy, easy }, 120);
        Console.WriteLine($"  hard vs 3 easy: hard={hve.Scores[0]:F2} easy={hve.Scores.Skip(1).Average():F2} ({hve.Ms}ms)");
        Check(hve.Scores[0] > hve.Scores.Skip(1).Average() + 1.0, "hard beats easy");
        var nve = RunGames(new[] { normal, easy, easy, easy }, 120);
        Console.WriteLine($"  normal vs 3 easy: normal={nve.Scores[0]:F2} easy={nve.Scores.Skip(1).Average():F2}");
        Check(nve.Scores[0] > nve.Scores.Skip(1).Average() + 0.5, "normal beats easy");
        var hvn = RunGames(new[] { hard, normal, normal, normal }, 240);
        Console.WriteLine($"  hard vs 3 normal: hard={hvn.Scores[0]:F2} normal={hvn.Scores.Skip(1).Average():F2}");
        Check(hvn.Scores[0] > hvn.Scores.Skip(1).Average() + 0.3, "hard beats normal");
        // 調整に使っていない別シードで再検証（結果を報告するのみ）
        var hvnFresh = RunGames(new[] { hard, normal, normal, normal }, 400, seedOffset: 100_000);
        Console.WriteLine($"  [fresh seeds] hard vs 3 normal: hard={hvnFresh.Scores[0]:F2} normal={hvnFresh.Scores.Skip(1).Average():F2} (400 games)");
        var hveFresh = RunGames(new[] { hard, easy, easy, easy }, 200, seedOffset: 100_000);
        Console.WriteLine($"  [fresh seeds] hard vs 3 easy: hard={hveFresh.Scores[0]:F2} easy={hveFresh.Scores.Skip(1).Average():F2} (200 games)");

        // 性格の傾向
        Stats Same(NpcPersonality p) => RunGames(Enumerable.Repeat(new NpcProfile(NpcDifficulty.Normal, p), 4).ToArray(), 40);
        var steady = Same(NpcPersonality.Steady);
        var seeker = Same(NpcPersonality.RevolutionSeeker);
        var conserver = Same(NpcPersonality.Conserver);
        var aggressive = Same(NpcPersonality.Aggressive);
        double PassRate(Stats s) => s.Passes / (double)(s.Passes + s.Plays);
        Console.WriteLine($"  pass rate: steady={PassRate(steady):P0} seeker={PassRate(seeker):P0} conserver={PassRate(conserver):P0} aggressive={PassRate(aggressive):P0}");
        Console.WriteLine($"  revolutions/game: steady={steady.Revolutions / 40.0:F2} seeker={seeker.Revolutions / 40.0:F2} conserver={conserver.Revolutions / 40.0:F2} aggressive={aggressive.Revolutions / 40.0:F2}");
        Check(PassRate(conserver) > PassRate(steady) && PassRate(steady) > PassRate(aggressive), "pass tendency conserver > steady > aggressive");
        Check(seeker.Revolutions > steady.Revolutions, "seeker revolts more");
    }
}
