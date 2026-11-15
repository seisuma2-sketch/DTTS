using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

namespace DTTS.Core.Ai;

/// <summary>
/// NPCの思考実行タイミングを決めるスケジューラー。
/// UI層では DispatcherQueueTimer 等のワンショットタイマーで「考え中」の演出時間を挟んで実装する。
/// Core は待機処理を一切持たない（ポーリング禁止の方針）。
/// </summary>
public interface INpcScheduler
{
    /// <summary>指定時間後に処理を1回実行する。</summary>
    void Schedule(TimeSpan delay, Action action);
}

/// <summary>遅延なしで即時実行するスケジューラー（テスト・高速シミュレーション用）。</summary>
public sealed class ImmediateNpcScheduler : INpcScheduler
{
    /// <summary>共有インスタンス。</summary>
    public static ImmediateNpcScheduler Instance { get; } = new();

    /// <inheritdoc />
    public void Schedule(TimeSpan delay, Action action) => action();
}

/// <summary>ゲームの進行スピード（NPCが考える時間の長さ）。</summary>
public enum GameSpeed : byte
{
    /// <summary>ゆっくり：何が起きたかを1手ずつ追える。</summary>
    Slow = 0,
    /// <summary>普通。</summary>
    Normal = 1,
    /// <summary>速い。</summary>
    Fast = 2,
    /// <summary>超速：ほぼ待ち時間なし。</summary>
    VeryFast = 3,
}

/// <summary>NPCの「考え中」演出の時間設定。</summary>
public sealed record NpcTimingOptions
{
    /// <summary>進行スピードに対応する時間設定を返す。</summary>
    public static NpcTimingOptions ForSpeed(GameSpeed speed) => speed switch
    {
        GameSpeed.Slow => new NpcTimingOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1900),
            Jitter = TimeSpan.FromMilliseconds(700),
            SideDecisionDelay = TimeSpan.FromMilliseconds(1300),
        },
        GameSpeed.Fast => new NpcTimingOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(500),
            Jitter = TimeSpan.FromMilliseconds(300),
            SideDecisionDelay = TimeSpan.FromMilliseconds(350),
        },
        GameSpeed.VeryFast => new NpcTimingOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(140),
            Jitter = TimeSpan.FromMilliseconds(80),
            SideDecisionDelay = TimeSpan.FromMilliseconds(100),
        },
        _ => new NpcTimingOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1000),
            Jitter = TimeSpan.FromMilliseconds(500),
            SideDecisionDelay = TimeSpan.FromMilliseconds(700),
        },
    };

    /// <summary>待ち時間なし（自分があがった後、残りの対戦をスキップして結果へ進むとき）。</summary>
    public static NpcTimingOptions Instant { get; } = new()
    {
        BaseDelay = TimeSpan.Zero,
        Jitter = TimeSpan.Zero,
        SideDecisionDelay = TimeSpan.Zero,
    };

    /// <summary>進行スピードの表示名。</summary>
    public static string SpeedName(GameSpeed speed) => speed switch
    {
        GameSpeed.Slow => "ゆっくり",
        GameSpeed.Fast => "速い",
        GameSpeed.VeryFast => "超速",
        _ => "普通",
    };

    /// <summary>基本の思考時間。</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>思考時間に加えるランダム幅（0〜この値）。</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>カード交換・宣言など手番以外の判断の思考時間。</summary>
    public TimeSpan SideDecisionDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// NPCとエンジンを接続するコントローラー。
/// <para>
/// エンジンの <see cref="DecisionRequestedEvent"/> を購読し、操作主体が <see cref="PlayerKind.Npc"/> のプレイヤーに
/// 割り当てた戦略で判断して応答する。場に出たカードを記録して、カードを記憶する思考（つよい）に渡す。
/// </para>
/// <para>
/// 通信切断時は <see cref="GameEngine.SetPlayerKind"/> で Npc に切り替えれば、エンジンが判断要求を再発行し、本コントローラーが引き継ぐ。
/// </para>
/// </summary>
public sealed class NpcController : IDisposable
{
    private readonly GameEngine _engine;
    private readonly INpcScheduler _scheduler;
    private NpcTimingOptions _timing;
    private readonly IRandomSource _random;
    private readonly Dictionary<int, INpcStrategy> _strategies = new();
    private readonly HashSet<Card> _seenCards = new();
    private PendingDecision? _lastHandled;
    private PendingDecision? _fallbackUsed;
    private bool _disposed;

    /// <summary>コントローラーを生成し、エンジンのイベント購読を開始する。</summary>
    /// <param name="engine">ゲームエンジン。</param>
    /// <param name="scheduler">思考実行のスケジューラー（null なら即時実行）。</param>
    /// <param name="timing">思考時間の設定。</param>
    /// <param name="random">思考時間のばらつき用乱数源。</param>
    public NpcController(GameEngine engine, INpcScheduler? scheduler = null, NpcTimingOptions? timing = null, IRandomSource? random = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _scheduler = scheduler ?? ImmediateNpcScheduler.Instance;
        _timing = timing ?? new NpcTimingOptions();
        _random = random ?? new SystemRandomSource();
        _engine.EventOccurred += OnEngineEvent;
    }

    /// <summary>
    /// 思考時間の設定。対戦中に変更でき、次に予約する判断から反映される（予約済みの判断は元の時間で実行される）。
    /// </summary>
    public NpcTimingOptions Timing
    {
        get => _timing;
        set => _timing = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>判断時に例外が発生した（フォールバックで応答済み）。ログ・フィードバック送信用。</summary>
    public event EventHandler<Exception>? StrategyFailed;

    /// <summary>このラウンドで公開されたカード。</summary>
    public IReadOnlyCollection<Card> SeenCards => _seenCards;

    /// <summary>プレイヤーに思考戦略を割り当てる。そのプレイヤーが判断待ちなら直ちに処理する。</summary>
    public void Assign(int playerId, INpcStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[playerId] = strategy;
        if (_engine.PendingDecision is { } d && d.PlayerId == playerId)
        {
            Handle(d);
        }
    }

    /// <summary>プロファイルから標準思考エンジンを生成して割り当てる。</summary>
    public void Assign(int playerId, NpcProfile profile, IRandomSource? random = null)
        => Assign(playerId, new StandardNpcBrain(profile, random));

    /// <summary>割り当てを解除する。</summary>
    public void Unassign(int playerId) => _strategies.Remove(playerId);

    /// <summary>割り当てられた戦略を取得する。</summary>
    public INpcStrategy? GetStrategy(int playerId) => _strategies.TryGetValue(playerId, out var s) ? s : null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _engine.EventOccurred -= OnEngineEvent;
    }

    private void OnEngineEvent(object? sender, GameEvent e)
    {
        switch (e)
        {
            case RoundStartedEvent:
                _seenCards.Clear();
                break;
            case CardsPlayedEvent played:
                _seenCards.UnionWith(played.Judgement.Play!.Cards);
                break;
            case CardsDiscardedEvent discarded:
                _seenCards.UnionWith(discarded.Cards);
                break;
            case DecisionRequestedEvent requested:
                Handle(requested.Decision);
                break;
            case CommandRejectedEvent rejected:
                OnRejected(rejected.Result);
                break;
        }
    }

    private bool IsControlled(int playerId)
        => _strategies.ContainsKey(playerId) && _engine.GetPlayer(playerId).Kind == PlayerKind.Npc;

    private void Handle(PendingDecision decision)
    {
        if (_disposed || !IsControlled(decision.PlayerId) || ReferenceEquals(decision, _lastHandled))
        {
            return;
        }
        _lastHandled = decision;

        // 割り込み（4止め・砂嵐）の確認は、止められる札を持っていてもいなくても同じ短い間で答える
        // （考える時間の長さで手札が透けないように。砂嵐は全員に確認が回るので、長いと待たされる）
        var delay = decision.Kind.IsPlayDecision() && !decision.Kind.IsInterruptChance()
            ? _timing.BaseDelay + TimeSpan.FromMilliseconds(_random.Next((int)Math.Max(1, _timing.Jitter.TotalMilliseconds)))
            : _timing.SideDecisionDelay;

        _scheduler.Schedule(delay, () => Execute(decision));
    }

    private void Execute(PendingDecision decision)
    {
        // 待っている間に状況が変わった（他の操作主体へ切替・ラウンド終了など）なら何もしない
        if (_disposed || !ReferenceEquals(_engine.PendingDecision, decision))
        {
            return;
        }
        if (!IsControlled(decision.PlayerId))
        {
            // 人間・通信へ切り替わった。再びNPCに戻されたとき同じ判断を処理できるよう記録を戻す
            if (ReferenceEquals(_lastHandled, decision))
            {
                _lastHandled = null;
            }
            return;
        }

        var strategy = _strategies[decision.PlayerId];
        CommandResult result;
        try
        {
            var context = NpcContext.FromEngine(_engine, decision.PlayerId, _seenCards);
            result = Submit(strategy, context, decision);
        }
        catch (Exception ex)
        {
            StrategyFailed?.Invoke(this, ex);
            result = SubmitFallback(decision);
        }

        if (!result.IsAccepted && !result.IsDeferred)
        {
            // 即時に拒否された場合（戦略の不具合）は安全な手で応答する
            SubmitFallback(decision);
        }
    }

    private CommandResult Submit(INpcStrategy strategy, NpcContext context, PendingDecision decision)
    {
        int id = decision.PlayerId;
        switch (decision.Kind)
        {
            case DecisionKind.PlayOrPass:
            case DecisionKind.FourStopChance:
            case DecisionKind.SandstormStopChance:
                var play = strategy.DecidePlay(context);
                return play.IsPass ? _engine.SubmitPass(id) : _engine.SubmitPlay(id, play.Cards!);
            case DecisionKind.SelectCards:
                return _engine.SubmitCardSelection(id, strategy.SelectCards(context));
            case DecisionKind.DeclareRanks:
                return _engine.SubmitRankDeclaration(id, strategy.DeclareRanks(context));
            case DecisionKind.RevolutionChoice:
                return _engine.SubmitRevolutionChoice(id, strategy.ChooseRevolution(context));
            default:
                throw new InvalidOperationException($"未対応の判断: {decision.Kind}");
        }
    }

    private void OnRejected(CommandResult result)
    {
        // 遅延実行された応答が拒否された：同じ判断がまだ残っていればフォールバックで応答する
        if (result.PlayerId is { } id
            && _engine.PendingDecision is { } d
            && d.PlayerId == id
            && ReferenceEquals(d, _lastHandled)
            && IsControlled(id))
        {
            StrategyFailed?.Invoke(this, new InvalidOperationException($"NPCの応答が拒否されました: {result}"));
            SubmitFallback(d);
        }
    }

    /// <summary>必ず受理される安全な応答（パス、出せる最初の手、最低限のカード選択）。判断ごとに1回だけ。</summary>
    private CommandResult SubmitFallback(PendingDecision decision)
    {
        if (ReferenceEquals(decision, _fallbackUsed))
        {
            return new CommandResult(CommandError.InvalidSelection, decision.PlayerId);
        }
        _fallbackUsed = decision;
        int id = decision.PlayerId;
        var player = _engine.GetPlayer(id);
        switch (decision.Kind)
        {
            case DecisionKind.PlayOrPass:
            case DecisionKind.FourStopChance:
            case DecisionKind.SandstormStopChance:
                if (decision.CanPass)
                {
                    return _engine.SubmitPass(id);
                }
                var legal = _engine.GetLegalPlays(id);
                return legal.Count > 0
                    ? _engine.SubmitPlay(id, legal[0].Play!.Cards)
                    : _engine.SubmitPass(id);
            case DecisionKind.SelectCards:
                return _engine.SubmitCardSelection(id, player.Hand.Take(decision.MinCount).ToList());
            case DecisionKind.DeclareRanks:
                return _engine.SubmitRankDeclaration(id, Enumerable.Range(1, decision.MinCount).Select(i => (Rank)i).ToList());
            case DecisionKind.RevolutionChoice:
                return _engine.SubmitRevolutionChoice(id, false);
            default:
                return new CommandResult(CommandError.InvalidSelection, id);
        }
    }
}
