using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Core.Logic;

/// <summary>参加プレイヤーの初期設定。</summary>
/// <param name="Name">表示名。</param>
/// <param name="Kind">操作主体。</param>
public sealed record PlayerSetup(string Name, PlayerKind Kind);

/// <summary>
/// 大富豪のゲーム進行エンジン（配牌・カード交換・ターン進行・場流れ・あがり・ラウンド管理・5R制＋延長戦）。
/// <para>
/// <b>完全イベント駆動</b>：エンジンは待機ループを持たない。コマンド（Submit 系）を受けると状態を進め、
/// 次にプレイヤーの判断が必要になった時点で <see cref="DecisionRequestedEvent"/> を発行して処理を止める。
/// UI・NPC・通信層はイベントを購読し、判断が決まったらコマンドを呼ぶ。
/// </para>
/// <para>
/// <b>再入安全</b>：イベントハンドラ内から呼ばれたコマンドはキューに積まれ、現在の処理完了後に順次実行される
/// （戻り値は <see cref="CommandError.Deferred"/>、拒否された場合は <see cref="CommandRejectedEvent"/> で通知）。
/// </para>
/// <para>
/// スレッドセーフではない。UIスレッド（またはテストの単一スレッド）から呼び出すこと。
/// </para>
/// </summary>
public sealed class GameEngine
{
    private RuleSettings _rules;
    private IJudgeLogic _judge;
    private readonly Func<RuleSettings, IJudgeLogic>? _judgeFactory;
    private readonly IRandomSource _random;
    private readonly List<Player> _players;
    private readonly List<int> _seatOrder;

    // --- 進行制御 ---
    private readonly LinkedList<Action> _steps = new();
    private readonly Queue<Action> _deferredCommands = new();
    private bool _busy;
    private long _eventSequence;

    // --- 判断待ちの継続処理 ---
    private Action<IReadOnlyList<Card>>? _cardSelectionHandler;
    private Action<IReadOnlyList<Rank>>? _rankDeclarationHandler;
    private Action<bool>? _revolutionChoiceHandler;

    // --- ラウンド内状態 ---
    private readonly HashSet<int> _passesSinceLastPlay = new();
    private readonly HashSet<int> _skippedSinceLastPlay = new();
    private readonly List<int> _finishOrder = new();
    private readonly List<int> _bottomOrder = new();
    private readonly Dictionary<int, FinishKind> _finishKinds = new();
    // 割り込み（4止め・砂嵐）の受付：機会を与える順番、場を流そうとしたプレイヤーとその効果
    private readonly Queue<int> _interruptQueue = new();
    private int? _interruptedPlayerId;
    private TriggeredEffect? _interruptedEffect;
    private DecisionKind _interruptKind;
    private int? _miyakoOchiPlayerId;
    private bool _gekokujo;

    /// <summary>「次の1手番だけ」の11バックが掛かっている手番のプレイヤー（Jの後、最初に手番が来た人。まだ来ていなければ null）。</summary>
    private int? _elevenBackTurnPlayerId;

    /// <summary>カード交換（天変地異を含む）で♦3を渡されたプレイヤーのID（いれば、その人から開始する）。</summary>
    private int? _diamondThreeRecipientId;

    // --- ラウンド間状態 ---
    private readonly List<RoundResult> _roundResults = new();
    private Dictionary<int, PlayerRank> _previousRanks = new();
    private readonly Dictionary<int, int> _consecutiveDaihinmin = new();
    private int? _lastRoundMiyakoOchiPlayerId;

    /// <summary>
    /// エンジンを生成する。
    /// </summary>
    /// <param name="rules">ルール設定（ゲーム中に変更されないよう内部で複製する）。</param>
    /// <param name="players">参加プレイヤー（2〜8人）。並び順が初期の座席順となり、IDは 0 から順に振られる。</param>
    /// <param name="random">乱数源（null なら非決定的な乱数）。</param>
    /// <param name="judgeFactory">判定ロジックの生成関数（テスト時の差し替え用）。</param>
    public GameEngine(
        RuleSettings rules,
        IReadOnlyList<PlayerSetup> players,
        IRandomSource? random = null,
        Func<RuleSettings, IJudgeLogic>? judgeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count < 2 || players.Count > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(players), "プレイヤー数は2〜8人です。");
        }

        _rules = rules.Clone();
        _rules.Normalize();
        _judgeFactory = judgeFactory;
        _judge = _judgeFactory?.Invoke(_rules) ?? new JudgeLogic(_rules);
        _random = random ?? new SystemRandomSource();
        _players = players.Select((p, i) => new Player(i, p.Name, p.Kind)).ToList();
        _seatOrder = _players.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// ゲームイベント。発行順に同期的に通知される。
    /// ハンドラ内からコマンドを呼んでもよい（現在の処理完了後に実行される）。
    /// </summary>
    public event EventHandler<GameEvent>? EventOccurred;

    // ======================================================================
    // 公開状態
    // ======================================================================

    /// <summary>このゲームで使用しているルール（開始時または途中変更後のスナップショット）。</summary>
    public RuleSettings Rules => _rules;

    /// <summary>対戦途中でルールを変更し、判定ロジックを差し替える。</summary>
    public void UpdateRules(RuleSettings newRules)
    {
        ArgumentNullException.ThrowIfNull(newRules);
        var cloned = newRules.Clone();
        cloned.Normalize();
        // イベント発火中の再入による判定器差し替えを防ぐため Run 経由で直列化する
        Run(() =>
        {
            _rules = cloned;
            _judge = _judgeFactory?.Invoke(_rules) ?? new JudgeLogic(_rules);
            return Ok();
        });
    }

    /// <summary>判定ロジック。</summary>
    public IJudgeLogic Judge => _judge;

    /// <summary>全プレイヤー（ID順）。</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>現在の座席順（プレイヤーIDの並び。時計回りはこの順）。</summary>
    public IReadOnlyList<int> SeatOrder => _seatOrder;

    /// <summary>現在のフェーズ。</summary>
    public GamePhase Phase { get; private set; } = GamePhase.NotStarted;

    /// <summary>現在のラウンド番号（1始まり）。</summary>
    public int RoundNumber { get; private set; }

    /// <summary>現在のラウンドが延長戦か。</summary>
    public bool IsExtraRound => RoundNumber > _rules.Progress.RoundCount;

    /// <summary>場の状態。</summary>
    public FieldState Field { get; private set; } = FieldState.CreateInitial();

    /// <summary>手番のプレイヤー（手番外の判断待ち中も直前の手番者を保持）。</summary>
    public int? CurrentPlayerId { get; private set; }

    /// <summary>現在求めている判断（なければ null）。</summary>
    public PendingDecision? PendingDecision { get; private set; }

    /// <summary>これまでのラウンド結果。</summary>
    public IReadOnlyList<RoundResult> RoundResults => _roundResults;

    /// <summary>プレイヤーを取得する。</summary>
    public Player GetPlayer(int playerId)
    {
        if (playerId < 0 || playerId >= _players.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(playerId));
        }
        return _players[playerId];
    }

    /// <summary>
    /// 指定プレイヤーが今出せる合法手を列挙する（手番または割り込み（4止め・砂嵐）の判断待ちでなければ空）。
    /// UIの「出せるカード」強調表示やNPCの候補生成に使用する。
    /// </summary>
    public IReadOnlyList<PlayJudgement> GetLegalPlays(int playerId)
    {
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId || !d.Kind.IsPlayDecision())
        {
            return Array.Empty<PlayJudgement>();
        }
        return _judge.GetLegalPlays(GetPlayer(playerId).Hand, Field);
    }

    // ======================================================================
    // コマンド
    // ======================================================================

    /// <summary>ゲームを開始する（ゲーム終了後に呼べば得点をリセットして再戦）。</summary>
    public CommandResult StartGame() => Run(() =>
    {
        if (Phase is not (GamePhase.NotStarted or GamePhase.GameEnded))
        {
            return Fail(CommandError.WrongPhase);
        }
        foreach (var p in _players)
        {
            p.TotalScore = 0;
            p.Rank = PlayerRank.None;
            p.FinishPosition = null;
        }
        _roundResults.Clear();
        _previousRanks = new Dictionary<int, PlayerRank>();
        _lastRoundMiyakoOchiPlayerId = null;
        _consecutiveDaihinmin.Clear();
        RoundNumber = 0;
        StartRound();
        RunSteps();
        return Ok();
    });

    /// <summary>次のラウンドを開始する（ラウンド終了フェーズでのみ有効）。</summary>
    public CommandResult StartNextRound() => Run(() =>
    {
        if (Phase != GamePhase.RoundEnded)
        {
            return Fail(CommandError.WrongPhase);
        }
        StartRound();
        RunSteps();
        return Ok();
    });

    /// <summary>カードを出す（手番、または4止め・砂嵐の割り込み）。</summary>
    public CommandResult SubmitPlay(int playerId, IEnumerable<Card> cards) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        if (Phase != GamePhase.Playing)
        {
            return Fail(CommandError.WrongPhase, playerId);
        }
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId || !d.Kind.IsPlayDecision())
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }
        var list = cards?.ToList() ?? new List<Card>();
        if (list.Count == 0)
        {
            return Fail(CommandError.InvalidSelection, playerId);
        }
        var player = GetPlayer(playerId);
        if (!player.HasAll(list))
        {
            return Fail(CommandError.CardsNotInHand, playerId);
        }
        var judgement = _judge.Judge(list, Field, list.Count == player.HandCount);
        if (!judgement.IsLegal)
        {
            return new CommandResult(CommandError.IllegalPlay, playerId, judgement.RejectReason);
        }

        PendingDecision = null;
        ApplyPlay(playerId, judgement);
        RunSteps();
        return Ok(playerId);
    });

    /// <summary>パスする（4止め・砂嵐の割り込みの判断待ちでは「見送り」）。</summary>
    public CommandResult SubmitPass(int playerId) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        if (Phase != GamePhase.Playing)
        {
            return Fail(CommandError.WrongPhase, playerId);
        }
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId)
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }

        if (d.Kind.IsInterruptChance())
        {
            PendingDecision = null;
            Emit(new PlayerPassedEvent(playerId, true));
            AskNextInterrupt();
            RunSteps();
            return Ok(playerId);
        }

        if (d.Kind != DecisionKind.PlayOrPass)
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }
        if (Field.IsEmpty)
        {
            return Fail(CommandError.CannotPassAsLeader, playerId);
        }
        var player = GetPlayer(playerId);
        if (!CanPass(player))
        {
            return Fail(CommandError.PassLimitReached, playerId);
        }

        PendingDecision = null;
        player.PassCountThisRound++;
        Field = Field.WithPass(playerId);
        _passesSinceLastPlay.Add(playerId);
        Emit(new PlayerPassedEvent(playerId, false));

        if (AllOthersPassed())
        {
            FlushAndLead(null);
        }
        else
        {
            var next = NextEligible(playerId);
            if (next is null || (Field.LastPlayerId is { } lastPlayer && next.Value == lastPlayer))
            {
                // 他の全員がパス済みまたはスキップされて最後に出した本人へ1周した（または本人があがり済みで他に誰も残っていない）ため、場を流して親にする
                FlushAndLead(null);
            }
            else
            {
                StartTurn(next.Value);
            }
        }
        RunSteps();
        return Ok(playerId);
    });

    /// <summary>カード選択に応答する（交換・7渡し・10捨て）。</summary>
    public CommandResult SubmitCardSelection(int playerId, IEnumerable<Card> cards) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId || d.Kind != DecisionKind.SelectCards || _cardSelectionHandler is null)
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }
        var list = cards?.ToList() ?? new List<Card>();
        if (!GetPlayer(playerId).HasAll(list))
        {
            return Fail(CommandError.CardsNotInHand, playerId);
        }
        if (list.Count < d.MinCount || list.Count > d.MaxCount)
        {
            return Fail(CommandError.InvalidSelection, playerId);
        }

        var handler = _cardSelectionHandler;
        ClearDecision();
        handler(list);
        RunSteps();
        return Ok(playerId);
    });

    /// <summary>12ボンバーの数字宣言に応答する（<see cref="Rank.Joker"/> を宣言するとジョーカーも捨てさせられる）。</summary>
    public CommandResult SubmitRankDeclaration(int playerId, IEnumerable<Rank> ranks) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId || d.Kind != DecisionKind.DeclareRanks || _rankDeclarationHandler is null)
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }
        var list = ranks?.ToList() ?? new List<Rank>();
        bool valid = list.Count >= d.MinCount
            && list.Count <= d.MaxCount
            && list.Distinct().Count() == list.Count
            && list.All(r => Enum.IsDefined(r));
        if (!valid)
        {
            return Fail(CommandError.InvalidSelection, playerId);
        }

        var handler = _rankDeclarationHandler;
        ClearDecision();
        handler(list);
        RunSteps();
        return Ok(playerId);
    });

    /// <summary>革命選択に応答する。</summary>
    /// <param name="playerId">プレイヤー。</param>
    /// <param name="applyRevolution">革命を起こすか。</param>
    public CommandResult SubmitRevolutionChoice(int playerId, bool applyRevolution) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        var d = PendingDecision;
        if (d is null || d.PlayerId != playerId || d.Kind != DecisionKind.RevolutionChoice || _revolutionChoiceHandler is null)
        {
            return Fail(CommandError.NotYourTurn, playerId);
        }
        var handler = _revolutionChoiceHandler;
        ClearDecision();
        handler(applyRevolution);
        RunSteps();
        return Ok(playerId);
    });

    /// <summary>
    /// プレイヤーの操作主体を変更する（通信切断時のNPC引き継ぎ等）。
    /// そのプレイヤーの判断待ちであれば判断要求を再発行し、新しい操作主体が応答できるようにする。
    /// </summary>
    public CommandResult SetPlayerKind(int playerId, PlayerKind kind) => Run(() =>
    {
        if (!IsValidPlayer(playerId))
        {
            return Fail(CommandError.UnknownPlayer, playerId);
        }
        var player = GetPlayer(playerId);
        if (player.Kind == kind)
        {
            return Ok(playerId);
        }
        player.Kind = kind;
        Emit(new PlayerKindChangedEvent(playerId, kind));
        if (PendingDecision is { } d && d.PlayerId == playerId)
        {
            Emit(new DecisionRequestedEvent(d));
        }
        return Ok(playerId);
    });

    /// <summary>現在の判断要求を再発行する（画面復帰・再接続時の再同期用）。</summary>
    public CommandResult ReissuePendingDecision() => Run(() =>
    {
        if (PendingDecision is { } d)
        {
            Emit(new DecisionRequestedEvent(d));
        }
        return Ok();
    });

    // ======================================================================
    // コマンド実行基盤（再入時は遅延実行）
    // ======================================================================

    private CommandResult Run(Func<CommandResult> command)
    {
        if (_busy)
        {
            _deferredCommands.Enqueue(() =>
            {
                var result = command();
                if (!result.IsAccepted)
                {
                    Emit(new CommandRejectedEvent(result));
                }
            });
            return new CommandResult(CommandError.Deferred);
        }

        _busy = true;
        try
        {
            var result = command();
            while (_deferredCommands.Count > 0)
            {
                _deferredCommands.Dequeue()();
            }
            return result;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>判断待ちになるか、ステップが尽きるまで継続処理を実行する。</summary>
    private void RunSteps()
    {
        while (PendingDecision is null
            && _steps.First is { } node
            && Phase is GamePhase.Dealing or GamePhase.Exchanging or GamePhase.Playing)
        {
            _steps.RemoveFirst();
            node.Value();
        }
    }

    /// <summary>ステップ列を「次に実行する処理」として先頭へ挿入する（順序は保持）。</summary>
    private void PushFront(IEnumerable<Action> actions)
    {
        foreach (var action in actions.Reverse())
        {
            _steps.AddFirst(action);
        }
    }

    private void Emit(GameEvent gameEvent)
    {
        gameEvent.Sequence = _eventSequence++;
        EventOccurred?.Invoke(this, gameEvent);
    }

    private void Request(PendingDecision decision)
    {
        PendingDecision = decision;
        Emit(new DecisionRequestedEvent(decision));
    }

    private void ClearDecision()
    {
        PendingDecision = null;
        _cardSelectionHandler = null;
        _rankDeclarationHandler = null;
        _revolutionChoiceHandler = null;
    }

    private void SetPhase(GamePhase phase)
    {
        if (Phase == phase)
        {
            return;
        }
        Phase = phase;
        Emit(new PhaseChangedEvent(phase));
    }

    private bool IsValidPlayer(int playerId) => playerId >= 0 && playerId < _players.Count;

    private static CommandResult Ok(int? playerId = null) => new(CommandError.None, playerId);

    private static CommandResult Fail(CommandError error, int? playerId = null) => new(error, playerId);

    // ======================================================================
    // ラウンド開始・配牌・カード交換
    // ======================================================================

    private void StartRound()
    {
        RoundNumber++;
        _steps.Clear();
        ClearDecision();
        _passesSinceLastPlay.Clear();
        _skippedSinceLastPlay.Clear();
        _finishOrder.Clear();
        _bottomOrder.Clear();
        _finishKinds.Clear();
        ClearInterrupt();
        _miyakoOchiPlayerId = null;
        _gekokujo = false;
        _elevenBackTurnPlayerId = null;
        _diamondThreeRecipientId = null;
        CurrentPlayerId = null;
        foreach (var p in _players)
        {
            p.ResetForRound();
        }

        switch (_rules.Progress.SeatShuffleMode)
        {
            case SeatShuffleMode.Random:
                _random.Shuffle(_seatOrder);
                break;
            case SeatShuffleMode.FromDaifugo when _previousRanks.Count > 0:
                _seatOrder.Sort((a, b) =>
                    ((int)_previousRanks.GetValueOrDefault(a, PlayerRank.Heimin))
                    .CompareTo((int)_previousRanks.GetValueOrDefault(b, PlayerRank.Heimin)));
                break;
            case SeatShuffleMode.FromDaihinmin when _previousRanks.Count > 0:
                _seatOrder.Sort((a, b) =>
                    ((int)_previousRanks.GetValueOrDefault(b, PlayerRank.Heimin))
                    .CompareTo((int)_previousRanks.GetValueOrDefault(a, PlayerRank.Heimin)));
                break;
        }

        Field = FieldState.CreateInitial();
        SetPhase(GamePhase.Dealing);
        Emit(new RoundStartedEvent(RoundNumber, IsExtraRound, _seatOrder.ToArray()));
        Deal();

        if (_previousRanks.Count > 0 && _rules.Exchange.Enabled)
        {
            SetPhase(GamePhase.Exchanging);
            _steps.AddLast(ExchangeStep);
        }
        _steps.AddLast(BeginPlay);
    }

    private void Deal()
    {
        var deck = Card.CreateDeck(_rules.Joker.JokerCount).ToList();

        // シャッフル方式
        switch (_rules.Progress.ShuffleMode)
        {
            case ShuffleMode.Separate2:
            {
                var low = deck.Where(c => !c.IsJoker && c.BaseStrength <= Card.GetBaseStrength(Rank.Nine)).ToList();
                var high = deck.Where(c => c.IsJoker || c.BaseStrength > Card.GetBaseStrength(Rank.Nine)).ToList();
                _random.Shuffle(low);
                _random.Shuffle(high);
                deck = new List<Card>(low.Count + high.Count);
                int max = Math.Max(low.Count, high.Count);
                for (int i = 0; i < max; i++)
                {
                    if (i < low.Count) deck.Add(low[i]);
                    if (i < high.Count) deck.Add(high[i]);
                }
                break;
            }
            case ShuffleMode.Separate3:
            {
                var g1 = deck.Where(c => !c.IsJoker && c.BaseStrength <= Card.GetBaseStrength(Rank.Six)).ToList();
                var g2 = deck.Where(c => !c.IsJoker && c.BaseStrength > Card.GetBaseStrength(Rank.Six) && c.BaseStrength <= Card.GetBaseStrength(Rank.Jack)).ToList();
                var g3 = deck.Where(c => c.IsJoker || c.BaseStrength > Card.GetBaseStrength(Rank.Jack)).ToList();
                _random.Shuffle(g1);
                _random.Shuffle(g2);
                _random.Shuffle(g3);
                deck = new List<Card>(g1.Count + g2.Count + g3.Count);
                int max = Math.Max(g1.Count, Math.Max(g2.Count, g3.Count));
                for (int i = 0; i < max; i++)
                {
                    if (i < g1.Count) deck.Add(g1[i]);
                    if (i < g2.Count) deck.Add(g2[i]);
                    if (i < g3.Count) deck.Add(g3[i]);
                }
                break;
            }
            case ShuffleMode.Reality:
            {
                // 同ランクの固まりを分散させるため、まず数字順に並べてからリフル混ぜ＋軽シャッフル
                var sorted = deck.OrderBy(c => c.IsJoker ? 99 : c.BaseStrength).ThenBy(c => (int)c.Suit).ToList();
                int half = sorted.Count / 2;
                var mixed = new List<Card>(sorted.Count);
                for (int i = 0; i < half; i++)
                {
                    mixed.Add(sorted[i]);
                    if (i + half < sorted.Count) mixed.Add(sorted[i + half]);
                }
                if (sorted.Count % 2 == 1) mixed.Add(sorted[^1]);
                // 局所的に入れ替えて自然な偏りを作る
                for (int i = 0; i < mixed.Count; i++)
                {
                    int j = Math.Clamp(i + _random.Next(5) - 2, 0, mixed.Count - 1);
                    (mixed[i], mixed[j]) = (mixed[j], mixed[i]);
                }
                deck = mixed;
                break;
            }
            default:
                _random.Shuffle(deck);
                break;
        }

        // ブラインドカード（枚数調整のため一部を除く）
        int seatCount = _seatOrder.Count;
        int removeCount = _rules.Progress.BlindCardMode switch
        {
            BlindCardMode.Remainder => deck.Count % seatCount,
            BlindCardMode.Max13 => Math.Max(0, deck.Count - (Math.Min(13, deck.Count / seatCount) * seatCount)),
            _ => 0,
        };
        if (removeCount > 0 && removeCount < deck.Count)
        {
            Card? protectedCard = _rules.Progress.FirstRoundStartCard switch
            {
                StartCardRule.DiamondThree => Card.Of(Suit.Diamond, Rank.Three),
                StartCardRule.SpadeThree => Card.Of(Suit.Spade, Rank.Three),
                StartCardRule.HeartThree => Card.Of(Suit.Heart, Rank.Three),
                StartCardRule.ClubThree => Card.Of(Suit.Club, Rank.Three),
                _ => null,
            };
            int removed = 0;
            for (int i = deck.Count - 1; i >= 0 && removed < removeCount; i--)
            {
                if (protectedCard is { } pc && deck[i] == pc) continue;
                deck.RemoveAt(i);
                removed++;
            }
        }

        var hands = _seatOrder.ToDictionary(id => id, _ => new List<Card>());
        // 配り始めの席をランダムにし、端数の1枚多い手札が特定の席に偏らないようにする
        int start = _random.Next(seatCount);
        for (int i = 0; i < deck.Count; i++)
        {
            hands[_seatOrder[(start + i) % seatCount]].Add(deck[i]);
        }
        foreach (var (id, cards) in hands)
        {
            GetPlayer(id).SetHand(cards);
        }
        Emit(new HandsDealtEvent(_players.ToDictionary(p => p.Id, p => p.HandCount)));
    }

    private Player? FindByPreviousRank(PlayerRank rank)
    {
        foreach (var (id, r) in _previousRanks)
        {
            if (r == rank)
            {
                return GetPlayer(id);
            }
        }
        return null;
    }

    /// <summary>天変地異の判定と、カード交換ステップの生成。</summary>
    private void ExchangeStep()
    {
        var ex = _rules.Exchange;
        var daifugo = FindByPreviousRank(PlayerRank.Daifugo);
        var daihinmin = FindByPreviousRank(PlayerRank.Daihinmin);
        var fugo = FindByPreviousRank(PlayerRank.Fugo);
        var hinmin = FindByPreviousRank(PlayerRank.Hinmin);

        // --- 天変地異（10以下条件、または5連続大貧民） ---
        bool fiveConsecutive = ex.FiveConsecutiveDaihinminTenpenChii
            && daihinmin is not null
            && _consecutiveDaihinmin.GetValueOrDefault(daihinmin.Id) >= 5;
        if (daifugo is not null && daihinmin is not null && ((ex.TenpenChiiEnabled && QualifiesForTenpenChii(daihinmin)) || fiveConsecutive))
        {
            var d3 = Card.Of(Suit.Diamond, Rank.Three);
            var richCards = daifugo.TakeAll();
            var poorCards = daihinmin.TakeAll();
            daifugo.SetHand(poorCards);
            daihinmin.SetHand(richCards);
            if (poorCards.Contains(d3)) _diamondThreeRecipientId = daifugo.Id;
            else if (richCards.Contains(d3)) _diamondThreeRecipientId = daihinmin.Id;
            Emit(new CardsTransferredEvent(daihinmin.Id, daifugo.Id, poorCards, CardTransferReason.TenpenChii));
            Emit(new CardsTransferredEvent(daifugo.Id, daihinmin.Id, richCards, CardTransferReason.TenpenChii));
            return; // 通常の交換は行わない
        }

        var actions = new List<Action>();
        bool skipDaifugoPair = !ex.LinkedToMiyakoOchi
            && daihinmin is not null
            && daihinmin.Id == _lastRoundMiyakoOchiPlayerId;
        if (daifugo is not null && daihinmin is not null && ex.DaifugoExchangeCount > 0 && !skipDaifugoPair)
        {
            actions.AddRange(ExchangePairSteps(daihinmin, daifugo, ex.DaifugoExchangeCount));
        }
        if (fugo is not null && hinmin is not null && ex.FugoExchangeCount > 0)
        {
            actions.AddRange(ExchangePairSteps(hinmin, fugo, ex.FugoExchangeCount));
        }
        PushFront(actions);
    }

    private bool QualifiesForTenpenChii(Player player)
    {
        int max = Card.GetBaseStrength(_rules.Exchange.TenpenChiiMaxRank);
        return player.HandCount > 0 && player.Hand.All(c => !c.IsJoker && c.BaseStrength <= max);
    }

    /// <summary>貧民側が渡す → 富豪側が返す、の2ステップを生成する。</summary>
    private IEnumerable<Action> ExchangePairSteps(Player poor, Player rich, int count)
    {
        yield return () =>
        {
            int n = Math.Min(count, poor.HandCount);
            if (n == 0)
            {
                return;
            }
            if (_rules.Exchange.PoorCanChooseCards)
            {
                RequestCardSelection(poor.Id, CardSelectionPurpose.ExchangeGive, n, n, rich.Id,
                    cards => Transfer(poor, rich, cards, CardTransferReason.Exchange));
            }
            else
            {
                Transfer(poor, rich, StrongestCards(poor, n, _rules.Exchange.JokerProtected), CardTransferReason.Exchange);
            }
        };
        yield return () =>
        {
            int n = Math.Min(count, rich.HandCount);
            if (n == 0)
            {
                return;
            }
            RequestCardSelection(rich.Id, CardSelectionPurpose.ExchangeGive, n, n, poor.Id,
                cards => Transfer(rich, poor, cards, CardTransferReason.Exchange));
        };
    }

    /// <summary>手札の強い順（通常時の序列、ジョーカー最強）に n 枚を選ぶ。</summary>
    private static IReadOnlyList<Card> StrongestCards(Player player, int n, bool protectJokers)
    {
        var candidates = player.Hand.Where(c => !(protectJokers && c.IsJoker)).ToList();
        if (candidates.Count < n)
        {
            candidates = player.Hand.ToList(); // 保護しきれない場合はジョーカーも対象
        }
        return candidates
            .OrderByDescending(c => c.BaseStrength)
            .ThenBy(c => c.Id)
            .Take(n)
            .ToList();
    }

    private void Transfer(Player from, Player to, IReadOnlyList<Card> cards, CardTransferReason reason)
    {
        if (cards.Count == 0)
        {
            return;
        }
        from.RemoveCards(cards);
        to.AddCards(cards);
        if (reason == CardTransferReason.Exchange && cards.Contains(Card.Of(Suit.Diamond, Rank.Three)))
        {
            _diamondThreeRecipientId = to.Id;
        }
        Emit(new CardsTransferredEvent(from.Id, to.Id, cards.ToArray(), reason));
    }

    private void RequestCardSelection(
        int playerId, CardSelectionPurpose purpose, int min, int max, int? target, Action<IReadOnlyList<Card>> handler)
    {
        _cardSelectionHandler = handler;
        Request(new PendingDecision(DecisionKind.SelectCards, playerId)
        {
            Purpose = purpose,
            MinCount = min,
            MaxCount = max,
            TargetPlayerId = target,
        });
    }

    // ======================================================================
    // ターン進行
    // ======================================================================

    private void BeginPlay()
    {
        SetPhase(GamePhase.Playing);
        int leader;
        var diamondThree = Card.Of(Suit.Diamond, Rank.Three);

        if (_previousRanks.Count == 0)
        {
            // 第1ラウンド
            Card? startCard = _rules.Progress.FirstRoundStartCard switch
            {
                StartCardRule.DiamondThree => diamondThree,
                StartCardRule.SpadeThree => Card.Of(Suit.Spade, Rank.Three),
                StartCardRule.HeartThree => Card.Of(Suit.Heart, Rank.Three),
                StartCardRule.ClubThree => Card.Of(Suit.Club, Rank.Three),
                _ => null,
            };
            var holder = startCard is { } sc ? _players.FirstOrDefault(p => p.Hand.Contains(sc)) : null;
            if (holder is not null)
            {
                leader = holder.Id;
                Field = FieldState.CreateInitial(PlayDirection.Clockwise);
            }
            else
            {
                leader = _seatOrder[_random.Next(_seatOrder.Count)];
            }
        }
        else
        {
            leader = _diamondThreeRecipientId ?? (_rules.Progress.LaterRoundLeader switch
            {
                RoundLeaderRule.Daifugo => FindByPreviousRank(PlayerRank.Daifugo)?.Id,
                RoundLeaderRule.Daihinmin => FindByPreviousRank(PlayerRank.Daihinmin)?.Id,
                RoundLeaderRule.DiamondThreeHolder => _players.FirstOrDefault(p => p.Hand.Contains(diamondThree))?.Id,
                _ => null,
            }) ?? _seatOrder[_random.Next(_seatOrder.Count)];
        }

        StartTurn(leader);
    }

    private void StartTurn(int playerId)
    {
        CurrentPlayerId = playerId;
        if (Field.IsElevenBackOneTurn && _elevenBackTurnPlayerId is null)
        {
            // Jの後、最初に手番が来た人：この人が出す・パスするまで11バックが続く
            _elevenBackTurnPlayerId = playerId;
        }
        var player = GetPlayer(playerId);

        // 親番なのに出せる手が1つもない（禁止あがり「その手ではあがれない」設定で、残り札が禁止カードのみ等）
        // → 出すこともパスもできないため反則負けとして手札を没収し、次のプレイヤーを親にする
        if (Field.IsEmpty && !_judge.GetLegalPlays(player.Hand, Field).Any())
        {
            var confiscated = player.TakeAll();
            if (confiscated.Count > 0)
            {
                Emit(new CardsDiscardedEvent(playerId, confiscated, CardDiscardReason.ForcedFoul));
            }
            FinishPlayer(playerId, FinishKind.Foul);
            if (Phase == GamePhase.Playing && NextActive(playerId) is { } next)
            {
                StartTurn(next);
            }
            return;
        }

        Request(new PendingDecision(DecisionKind.PlayOrPass, playerId)
        {
            CanPass = !Field.IsEmpty && CanPass(player),
        });
    }

    private bool CanPass(Player player)
    {
        int limit = _rules.Progress.PassLimitPerRound;
        if (limit <= 0 || player.PassCountThisRound < limit)
        {
            return true;
        }
        // 上限到達でも出せる手が1つもなければパスを認める
        return !_judge.GetLegalPlays(player.Hand, Field).Any();
    }

    /// <summary>
    /// このプレイヤーが現在の場でパス扱いか。
    /// パスはパス固定ルール時は場が流れるまで、通常は直前の出し以降。スキップは常に直前の出し以降のみ。
    /// </summary>
    private bool HasRealPassed(int playerId)
        => _rules.Progress.PassLocksUntilFlush ? Field.HasPassed(playerId) : _passesSinceLastPlay.Contains(playerId);

    private bool IsPassedForTurn(int playerId)
        => _skippedSinceLastPlay.Contains(playerId) || HasRealPassed(playerId);

    /// <summary>最後に出したプレイヤー以外の、残っている全員が実際にパスしたか（スキップは含めない）。</summary>
    private bool AllOthersPassed()
    {
        int? last = Field.LastPlayerId;
        if (last is null)
        {
            return false;
        }
        return _players.Where(p => !p.IsFinished && p.Id != last).All(p => HasRealPassed(p.Id));
    }

    /// <summary>進行方向に沿って、from の次に位置する（あがっていない）プレイヤーを列挙する。</summary>
    private IEnumerable<int> ActivePlayersAfter(int fromPlayerId)
    {
        int n = _seatOrder.Count;
        int index = _seatOrder.IndexOf(fromPlayerId);
        int step = Field.Direction == PlayDirection.Clockwise ? 1 : -1;
        for (int k = 1; k <= n; k++)
        {
            int id = _seatOrder[(((index + (step * k)) % n) + n) % n];
            if (!GetPlayer(id).IsFinished)
            {
                yield return id;
            }
        }
    }

    /// <summary>from の次の、パス扱いでないプレイヤー（いなければ null）。</summary>
    private int? NextEligible(int fromPlayerId)
    {
        foreach (int id in ActivePlayersAfter(fromPlayerId))
        {
            if (!IsPassedForTurn(id))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>from の次の、あがっていないプレイヤー（自分自身を除く。いなければ null）。</summary>
    private int? NextActive(int fromPlayerId)
    {
        foreach (int id in ActivePlayersAfter(fromPlayerId))
        {
            if (id != fromPlayerId)
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>
    /// 「次の手番だけ」の11バックで、誰かがカードを出し終えたら解除する（パスでは消えない）。
    /// </summary>
    /// <returns>解除したら true。</returns>
    private bool ExpireOneTurnElevenBack(int playerId)
    {
        if (!Field.IsElevenBackOneTurn)
        {
            return false;
        }
        Field = Field.WithElevenBackCleared();
        _elevenBackTurnPlayerId = null;
        return true;
    }

    /// <summary>場を流し、最後に出したプレイヤー（あがっていれば次のプレイヤー）を親にする。</summary>
    private void FlushAndLead(SpecialEffectType? cause, int? lastPlayerOverride = null)
    {
        int? last = lastPlayerOverride ?? Field.LastPlayerId;
        Field = Field.Flushed();
        _passesSinceLastPlay.Clear();
        _skippedSinceLastPlay.Clear();

        int? leader = null;
        if (last is { } l)
        {
            leader = GetPlayer(l).IsFinished ? NextActive(l) : l;
        }
        leader ??= _players.FirstOrDefault(p => !p.IsFinished)?.Id;

        Emit(new FieldFlushedEvent(leader, cause, Field));
        if (leader is { } next)
        {
            StartTurn(next);
        }
    }

    // ======================================================================
    // 出し札の適用と特殊効果
    // ======================================================================

    private void ApplyPlay(int playerId, PlayJudgement judgement)
    {
        var player = GetPlayer(playerId);
        var play = judgement.Play!;

        player.RemoveCards(play.Cards);
        Field = Field.WithPlay(playerId, play, judgement.NextLock);
        _passesSinceLastPlay.Clear();
        _skippedSinceLastPlay.Clear();
        if (judgement.BeatMethod is BeatMethod.FourStop or BeatMethod.SandstormStop)
        {
            // 割り込みが成立：止められた効果（8切り・ろくろ首・99車）は発動しない
            ClearInterrupt();
        }
        if (judgement.BeatMethod == BeatMethod.FourStop && !_rules.Flow.FourStopFlushesField)
        {
            // 4止めの後は、場が流れるまで砂嵐しか出せない（ほかの人はパスするか砂嵐で流す）
            Field = Field.WithFourStopLocked(true);
        }
        // 「次の1手番だけ」の11バック：この人が出し終えたので解除する（出した札は反転中の強さで判定済み）。
        // この出し札にJが含まれていれば、下の効果処理で改めて「次の人だけ」の11バックが掛かる
        bool elevenBackExpired = ExpireOneTurnElevenBack(playerId);
        CurrentPlayerId = playerId;
        Emit(new CardsPlayedEvent(playerId, judgement, Field));
        if (elevenBackExpired)
        {
            Emit(new ElevenBackExpiredEvent(playerId, Field));
        }

        // --- あがり判定（効果適用より先に順位を確定させる） ---
        if (player.HandCount == 0)
        {
            bool foul = judgement.IsForbiddenFinish && _rules.ForbiddenFinish.Penalty == ForbiddenFinishPenalty.LastPlace;
            FinishPlayer(playerId, foul ? FinishKind.Foul : FinishKind.Normal);
            if (Phase != GamePhase.Playing)
            {
                return;
            }
        }

        var steps = new List<Action>();
        TriggeredEffect? flushEffect = null;
        int skipCount = 0;
        int reverseCount = 0;

        foreach (var effect in judgement.Effects)
        {
            var e = effect;
            if (e.IsRevolutionTrigger)
            {
                steps.Add(() => HandleRevolution(playerId, e));
                continue;
            }
            switch (e.Type)
            {
                case SpecialEffectType.ElevenBack:
                    steps.Add(() =>
                    {
                        Field = Field.WithElevenBackToggled(
                            _rules.ElevenBack.Duration == ElevenBackDuration.Persistent,
                            oneTurn: _rules.ElevenBack.Duration == ElevenBackDuration.NextTurnOnly);
                        _elevenBackTurnPlayerId = null;
                        Emit(new EffectAppliedEvent(playerId, e, true, Field));
                    });
                    break;
                case SpecialEffectType.StrongElevenBack:
                    steps.Add(() =>
                    {
                        // 強化11バック（Jが3枚）：次の手番が11バックの効果になる
                        Field = Field.WithElevenBackToggled(
                            persistent: false,
                            oneTurn: true);
                        _elevenBackTurnPlayerId = null;
                        Emit(new EffectAppliedEvent(playerId, e, true, Field));
                    });
                    break;
                case SpecialEffectType.SevenPass:
                    steps.Add(() => RequestSevenPass(playerId, e));
                    break;
                case SpecialEffectType.TenDiscard:
                    steps.Add(() => RequestTenDiscard(playerId, e));
                    break;
                case SpecialEffectType.TwelveBomber:
                    steps.Add(() => RequestTwelveBomber(playerId, e));
                    break;
                case SpecialEffectType.FiveSkip:
                case SpecialEffectType.ThirteenSkip:
                    skipCount += e.Magnitude;
                    steps.Add(() => Emit(new EffectAppliedEvent(playerId, e, true, Field)));
                    break;
                case SpecialEffectType.NineReverse:
                case SpecialEffectType.TwelveReverse:
                    reverseCount += e.Magnitude;
                    break;
                default:
                    if (e.FlushesField || (e.Type == SpecialEffectType.FourStop && _rules.Flow.FourStopFlushesField))
                    {
                        flushEffect ??= e;
                    }
                    else
                    {
                        // 縛り成立・4止め・ダウンナンバー等の通知のみの効果
                        steps.Add(() => Emit(new EffectAppliedEvent(playerId, e, true, Field)));
                    }
                    break;
            }
        }

        int skip = skipCount;
        int reverse = reverseCount;
        var flush = flushEffect;
        var reverseEffect = judgement.Effects.FirstOrDefault(x => x.Type is SpecialEffectType.NineReverse or SpecialEffectType.TwelveReverse);
        steps.Add(() => ConcludePlay(playerId, flush, skip, reverse, reverseEffect));
        PushFront(steps);
    }

    private void HandleRevolution(int playerId, TriggeredEffect effect)
    {
        if (!effect.IsOptional)
        {
            ApplyRevolution(playerId, effect);
            return;
        }
        _revolutionChoiceHandler = apply =>
        {
            if (apply)
            {
                ApplyRevolution(playerId, effect);
            }
            else
            {
                Emit(new EffectAppliedEvent(playerId, effect, false, Field));
            }
        };
        Request(new PendingDecision(DecisionKind.RevolutionChoice, playerId) { Effect = effect.Type, CanPass = false });
    }

    private void ApplyRevolution(int playerId, TriggeredEffect effect)
    {
        Field = Field.WithRevolutionToggled(effect.Type == SpecialEffectType.Omen);
        if (effect.Type == SpecialEffectType.Coup)
        {
            // クーデター成立後は場が流れるまで6・7・砂嵐のみ出せるようにロックする
            Field = Field.WithCoupLocked(true);
        }
        Emit(new EffectAppliedEvent(playerId, effect, true, Field));
    }

    private void RequestSevenPass(int playerId, TriggeredEffect effect)
    {
        var player = GetPlayer(playerId);
        int? target = NextActive(playerId);
        int n = Math.Min(effect.Magnitude, player.HandCount);
        if (target is null || n == 0 || player.IsFinished)
        {
            return;
        }
        var receiver = GetPlayer(target.Value);
        int min = _rules.Special.SevenPassAllowZero ? 0 : n;
        RequestCardSelection(playerId, CardSelectionPurpose.SevenPass, min, n, receiver.Id, cards =>
        {
            if (cards.Count > 0)
            {
                Transfer(player, receiver, cards, CardTransferReason.SevenPass);
            }
            Emit(new EffectAppliedEvent(playerId, effect, cards.Count > 0, Field));
            if (player.HandCount == 0)
            {
                bool foul = _rules.ForbiddenFinish.Enabled && _rules.ForbiddenFinish.ForbidSevenPass;
                FinishPlayer(playerId, foul ? FinishKind.Foul : FinishKind.Normal);
            }
        });
    }

    private void RequestTenDiscard(int playerId, TriggeredEffect effect)
    {
        var player = GetPlayer(playerId);
        int n = Math.Min(effect.Magnitude, player.HandCount);
        if (n == 0 || player.IsFinished)
        {
            return;
        }
        int min = _rules.Special.TenDiscardAllowZero ? 0 : n;
        RequestCardSelection(playerId, CardSelectionPurpose.TenDiscard, min, n, null, cards =>
        {
            if (cards.Count > 0)
            {
                player.RemoveCards(cards);
                Emit(new CardsDiscardedEvent(playerId, cards.ToArray(), CardDiscardReason.TenDiscard));
            }
            Emit(new EffectAppliedEvent(playerId, effect, cards.Count > 0, Field));
            if (player.HandCount == 0)
            {
                bool foul = _rules.ForbiddenFinish.Enabled && _rules.ForbiddenFinish.ForbidTenDiscard;
                FinishPlayer(playerId, foul ? FinishKind.Foul : FinishKind.Normal);
            }
        });
    }

    /// <summary>12ボンバーで宣言できる数字の種類の上限（A〜K の13種＋ジョーカー）。</summary>
    public const int MaxDeclarableRanks = Card.RanksPerSuit + 1;

    private void RequestTwelveBomber(int playerId, TriggeredEffect effect)
    {
        // Qを出してそのままあがった場合は宣言・カード消去を行わない
        if (GetPlayer(playerId).IsFinished)
        {
            return;
        }
        // 宣言できるのは 13種の数字＋ジョーカーの最大14種
        int n = Math.Min(effect.Magnitude, MaxDeclarableRanks);
        _rankDeclarationHandler = ranks =>
        {
            Emit(new RanksDeclaredEvent(playerId, ranks.ToArray()));
            var declared = new HashSet<Rank>(ranks);
            // 宣言者から進行方向順に処理（あがりの順位もこの順で確定）
            var order = new List<int> { playerId };
            order.AddRange(ActivePlayersAfter(playerId).Where(id => id != playerId));
            foreach (int id in order)
            {
                var p = GetPlayer(id);
                if (p.IsFinished)
                {
                    continue;
                }
                // ジョーカーの数字は Rank.Joker。宣言に含まれていればジョーカーも捨てさせる
                var hit = p.Hand.Where(c => declared.Contains(c.Rank)).ToList();
                if (hit.Count > 0)
                {
                    p.RemoveCards(hit);
                    Emit(new CardsDiscardedEvent(id, hit, CardDiscardReason.TwelveBomber));
                }
            }
            Emit(new EffectAppliedEvent(playerId, effect, true, Field));
            foreach (int id in order)
            {
                if (Phase != GamePhase.Playing)
                {
                    break;
                }
                var p = GetPlayer(id);
                if (!p.IsFinished && p.HandCount == 0)
                {
                    bool foul = id != playerId
                        && _rules.ForbiddenFinish.Enabled
                        && _rules.ForbiddenFinish.TwelveBomberFinishRule == GentleStrictRule.Strict;
                    FinishPlayer(id, foul ? FinishKind.Foul : FinishKind.Normal);
                }
            }
        };
        Request(new PendingDecision(DecisionKind.DeclareRanks, playerId)
        {
            MinCount = n,
            MaxCount = n,
            Effect = SpecialEffectType.TwelveBomber,
        });
    }

    /// <summary>出し札の後処理：リバース → 場流し（または4止め・砂嵐の割り込み受付）→ スキップ → 次の手番。</summary>
    private void ConcludePlay(int playerId, TriggeredEffect? flushEffect, int skipCount, int reverseCount, TriggeredEffect? reverseEffect)
    {
        bool doReverse = _rules.SkipReverse.ReverseMultipleCountsAsOne
            ? reverseCount > 0
            : reverseCount % 2 == 1;
        if (doReverse)
        {
            Field = Field.WithDirectionReversed();
        }
        if (reverseEffect is not null)
        {
            Emit(new EffectAppliedEvent(playerId, reverseEffect, doReverse, Field));
        }

        if (flushEffect is not null)
        {
            // 砂嵐・スペ3返し以外の流し役は、止められる人に割り込みの機会を与えてから流す
            if (TryOpenInterruptWindow(playerId, flushEffect))
            {
                return;
            }
            Emit(new EffectAppliedEvent(playerId, flushEffect, true, Field));
            FlushAndLead(flushEffect.Type);
            return;
        }

        // あがり流し：あがった人のカードは残さず、次のプレイヤーが親になる
        // （出した札・7渡し・10捨てであがった場合。8切り等の流し役は上で処理済み）
        if (GetPlayer(playerId).IsFinished && !_rules.Progress.KeepFieldOnPlayerFinish)
        {
            FlushAndLead(null);
            return;
        }

        if (skipCount > 0)
        {
            var skipped = ActivePlayersAfter(playerId)
                .Where(id => id != playerId && !IsPassedForTurn(id))
                .Take(skipCount)
                .ToList();
            foreach (int id in skipped)
            {
                // スキップは次の出しまでの一時的なパス扱い（パス履歴・パス回数には含めない）
                _skippedSinceLastPlay.Add(id);
            }
            if (skipped.Count > 0)
            {
                Emit(new PlayersSkippedEvent(playerId, skipped));
            }
        }

        if (AllOthersPassed())
        {
            FlushAndLead(null);
            return;
        }
        var next = NextEligible(playerId);
        if (next is null || next.Value == playerId)
        {
            // 他の全員がパス済みまたは今回スキップされて手番が1周したため、場を流して自分が親になる
            FlushAndLead(null);
            return;
        }
        StartTurn(next.Value);
    }

    // ======================================================================
    // 割り込み（8切りへの4止め／ろくろ首・99車への砂嵐）
    // ======================================================================

    /// <summary>
    /// 止められる手を持っていなくても、割り込み（4止め・砂嵐）の確認を出すプレイヤー
    /// （操作ガイドが「初心者用」の人間プレイヤー）。ここにいない人は、実際に止められる手を持つときだけ聞かれる。
    /// </summary>
    public ISet<int> AlwaysAskInterruptPlayerIds { get; } = new HashSet<int>();

    /// <summary>
    /// 場を流す効果に割り込める手段を返す（4止め・砂嵐）。
    /// 砂嵐そのものは誰にも止められない最上位の流し役なので、割り込みは受け付けない。スペ3返しも即座に流れる。
    /// </summary>
    private (bool FourStop, bool Sandstorm) InterruptMeansFor(TriggeredEffect effect)
    {
        var flow = _rules.Flow;
        // 階段の8切りは4止めでは止められない（砂嵐なら止められる）
        bool fourStop = effect.Type == SpecialEffectType.EightCut
            && flow.FourStopEnabled
            && Field.CurrentPlay is { IsSequenceLike: false };
        bool sandstorm = flow.SandstormEnabled && effect.Type is
            SpecialEffectType.EightCut or SpecialEffectType.Rokurokubi or
            SpecialEffectType.Ambulance or SpecialEffectType.Emperor;
        return (fourStop, sandstorm);
    }

    /// <summary>
    /// 場を流す効果の発動を保留し、他のプレイヤーへ進行方向順に割り込みの機会を与える。
    /// 機会を与えるのは、実際に止められる手（4止め・砂嵐）を持つ人と <see cref="AlwaysAskInterruptPlayerIds"/> の人。
    /// 誰もいなければ割り込みは起きず、すぐに効果が成立して場が流れる。
    /// </summary>
    /// <param name="flusherId">場を流そうとしているプレイヤー。</param>
    /// <param name="effect">保留する効果（8切り・ろくろ首・99車・エンペラー）。</param>
    /// <returns>割り込みの受付を始めたら true（このルールでは止められない効果なら false）。</returns>
    private bool TryOpenInterruptWindow(int flusherId, TriggeredEffect effect)
    {
        var (fourStop, sandstorm) = InterruptMeansFor(effect);
        if (!fourStop && !sandstorm)
        {
            return false;
        }
        Field = Field.WithEightCutPending(fourStop).WithSandstormStopPending(sandstorm);
        _interruptedPlayerId = flusherId;
        _interruptedEffect = effect;
        // 8切りは「4止めの機会」（砂嵐も受け付ける）、それ以外は「砂嵐の機会」として聞く
        _interruptKind = fourStop ? DecisionKind.FourStopChance : DecisionKind.SandstormStopChance;
        _interruptQueue.Clear();
        foreach (int id in ActivePlayersAfter(flusherId).Where(id => id != flusherId))
        {
            if (AlwaysAskInterruptPlayerIds.Contains(id) || _judge.GetLegalPlays(GetPlayer(id).Hand, Field).Count > 0)
            {
                _interruptQueue.Enqueue(id);
            }
        }
        AskNextInterrupt();
        return true;
    }

    private void AskNextInterrupt()
    {
        while (_interruptQueue.Count > 0)
        {
            int id = _interruptQueue.Dequeue();
            if (GetPlayer(id).IsFinished)
            {
                continue;
            }
            Request(new PendingDecision(_interruptKind, id)
            {
                CanPass = true,
                Effect = _interruptedEffect?.Type,
                TargetPlayerId = _interruptedPlayerId,
            });
            return;
        }

        // 誰も割り込まなかった：保留していた効果（8切り・ろくろ首・99車）が成立して場が流れる
        int? flusher = _interruptedPlayerId;
        var effect = _interruptedEffect;
        ClearInterrupt();
        Field = Field.WithEightCutPending(false).WithSandstormStopPending(false);
        if (flusher is { } f && effect is not null)
        {
            Emit(new EffectAppliedEvent(f, effect, true, Field));
        }
        FlushAndLead(effect?.Type, flusher);
    }

    private void ClearInterrupt()
    {
        _interruptQueue.Clear();
        _interruptedPlayerId = null;
        _interruptedEffect = null;
    }

    // ======================================================================
    // あがり・ラウンド終了・ゲーム終了
    // ======================================================================

    private void FinishPlayer(int playerId, FinishKind kind)
    {
        var player = GetPlayer(playerId);
        if (player.IsFinished || Phase != GamePhase.Playing)
        {
            return;
        }
        player.IsFinished = true;
        _finishKinds[playerId] = kind;
        if (kind is FinishKind.Foul or FinishKind.MiyakoOchi)
        {
            _bottomOrder.Add(playerId); // 先に下位確定した者ほど下の順位
        }
        else
        {
            _finishOrder.Add(playerId);
        }
        // 通常のあがりは上から、反則・都落ちは下から順位が埋まる
        int place = kind is FinishKind.Foul or FinishKind.MiyakoOchi
            ? _players.Count - _bottomOrder.Count + 1
            : _finishOrder.Count;
        Emit(new PlayerFinishedEvent(playerId, kind, place));

        // --- 1位あがり時の階級変動ルール ---
        if (kind == FinishKind.Normal && _finishOrder.Count == 1 && _previousRanks.Count > 0)
        {
            var rc = _rules.RankChange;
            if (rc.GekokujoEnabled && _previousRanks.TryGetValue(playerId, out var prev) && prev == PlayerRank.Daihinmin)
            {
                _gekokujo = true;
                EndRound();
                return;
            }
            if (rc.MiyakoOchiEnabled)
            {
                var formerDaifugo = FindByPreviousRank(PlayerRank.Daifugo);
                if (formerDaifugo is not null && formerDaifugo.Id != playerId && !formerDaifugo.IsFinished)
                {
                    var confiscated = formerDaifugo.TakeAll();
                    if (confiscated.Count > 0)
                    {
                        Emit(new CardsDiscardedEvent(formerDaifugo.Id, confiscated, CardDiscardReason.MiyakoOchi));
                    }
                    _miyakoOchiPlayerId = formerDaifugo.Id;
                    FinishPlayer(formerDaifugo.Id, FinishKind.MiyakoOchi);
                }
            }
        }

        CheckRoundEnd();
    }

    private void CheckRoundEnd()
    {
        if (Phase != GamePhase.Playing)
        {
            return;
        }
        var active = _players.Where(p => !p.IsFinished).ToList();
        if (active.Count > 1)
        {
            return;
        }
        if (active.Count == 1)
        {
            var last = active[0];
            last.IsFinished = true;
            _finishOrder.Add(last.Id);
            _finishKinds[last.Id] = FinishKind.LastRemaining;
            Emit(new PlayerFinishedEvent(last.Id, FinishKind.LastRemaining, _finishOrder.Count));
        }
        EndRound();
    }

    private void EndRound()
    {
        _steps.Clear();
        ClearDecision();
        ClearInterrupt();
        CurrentPlayerId = null;

        int n = _players.Count;
        List<int> order;
        if (_gekokujo)
        {
            // 下剋上：前ラウンドの階級を上下反転した順位で確定
            order = _players
                .OrderBy(p => (int)InvertRank(_previousRanks.TryGetValue(p.Id, out var r) ? r : PlayerRank.Heimin))
                .ThenBy(p => _seatOrder.IndexOf(p.Id))
                .Select(p => p.Id)
                .ToList();
        }
        else
        {
            order = _finishOrder.Concat(Enumerable.Reverse(_bottomOrder)).ToList();
            // 念のため未確定者を末尾に補完
            order.AddRange(_players.Select(p => p.Id).Where(id => !order.Contains(id)));
        }

        var standings = new List<RoundStanding>(n);
        for (int i = 0; i < order.Count; i++)
        {
            var player = GetPlayer(order[i]);
            var rank = RankForPosition(i + 1, n);
            int points = PointsFor(rank);
            player.Rank = rank;
            player.FinishPosition = i + 1;
            player.TotalScore += points;
            var kind = _gekokujo
                ? FinishKind.Gekokujo
                : (_finishKinds.TryGetValue(player.Id, out var k) ? k : FinishKind.LastRemaining);
            standings.Add(new RoundStanding(player.Id, i + 1, rank, points, kind));
        }

        _previousRanks = _players.ToDictionary(p => p.Id, p => p.Rank);
        foreach (var p in _players)
        {
            _consecutiveDaihinmin[p.Id] = p.Rank == PlayerRank.Daihinmin
                ? _consecutiveDaihinmin.GetValueOrDefault(p.Id) + 1
                : 0;
        }
        _lastRoundMiyakoOchiPlayerId = _miyakoOchiPlayerId;

        var result = new RoundResult(RoundNumber, IsExtraRound, standings, _gekokujo, _miyakoOchiPlayerId);
        _roundResults.Add(result);

        bool gameOver = IsGameOver();
        SetPhase(GamePhase.RoundEnded);
        Emit(new RoundEndedEvent(result, gameOver));

        if (gameOver)
        {
            SetPhase(GamePhase.GameEnded);
            Emit(new GameEndedEvent(BuildGameResult()));
        }
    }

    private bool IsGameOver()
    {
        var progress = _rules.Progress;
        if (RoundNumber < progress.RoundCount)
        {
            return false;
        }
        int top = _players.Max(p => p.TotalScore);
        int leaders = _players.Count(p => p.TotalScore == top);
        int extraPlayed = RoundNumber - progress.RoundCount;
        if (leaders > 1 && progress.ExtraRoundOnTie && extraPlayed < progress.MaxExtraRounds)
        {
            return false; // 同点首位のため延長戦
        }
        return true;
    }

    private GameResult BuildGameResult()
    {
        var ordered = _players.OrderByDescending(p => p.TotalScore).ThenBy(p => p.Id).ToList();
        var standings = new List<GameStanding>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            // 同点は同順位
            int place = i > 0 && ordered[i].TotalScore == ordered[i - 1].TotalScore ? standings[i - 1].Place : i + 1;
            standings.Add(new GameStanding(ordered[i].Id, ordered[i].Name, ordered[i].TotalScore, place));
        }
        var winners = standings.Where(s => s.Place == 1).Select(s => s.PlayerId).ToArray();
        return new GameResult(standings, winners, RoundNumber, _roundResults.ToArray());
    }

    /// <summary>順位から階級を求める（4人以上：大富豪・富豪・…平民…・貧民・大貧民）。</summary>
    public static PlayerRank RankForPosition(int position, int playerCount)
    {
        if (position == 1)
        {
            return PlayerRank.Daifugo;
        }
        if (position == playerCount)
        {
            return PlayerRank.Daihinmin;
        }
        if (playerCount >= 4 && position == 2)
        {
            return PlayerRank.Fugo;
        }
        if (playerCount >= 4 && position == playerCount - 1)
        {
            return PlayerRank.Hinmin;
        }
        return PlayerRank.Heimin;
    }

    private static PlayerRank InvertRank(PlayerRank rank) => rank switch
    {
        PlayerRank.Daifugo => PlayerRank.Daihinmin,
        PlayerRank.Fugo => PlayerRank.Hinmin,
        PlayerRank.Hinmin => PlayerRank.Fugo,
        PlayerRank.Daihinmin => PlayerRank.Daifugo,
        _ => rank,
    };

    private int PointsFor(PlayerRank rank)
    {
        var p = _rules.Progress;
        return rank switch
        {
            PlayerRank.Daifugo => p.PointsDaifugo,
            PlayerRank.Fugo => p.PointsFugo,
            PlayerRank.Hinmin => p.PointsHinmin,
            PlayerRank.Daihinmin => p.PointsDaihinmin,
            _ => p.PointsHeimin,
        };
    }
}
