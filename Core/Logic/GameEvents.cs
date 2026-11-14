using System;
using System.Collections.Generic;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Core.Logic;

// ======================================================================
// 判断要求・コマンド結果
// ======================================================================

/// <summary>
/// エンジンがプレイヤーに求めている判断。同時に存在する判断は常に1つだけ。
/// UI・NPC・通信層は <see cref="DecisionRequestedEvent"/> を購読し、対応する Submit 系コマンドで応答する。
/// </summary>
/// <param name="Kind">判断の種類。</param>
/// <param name="PlayerId">判断するプレイヤー。</param>
public sealed record PendingDecision(DecisionKind Kind, int PlayerId)
{
    /// <summary>カード選択の目的（<see cref="DecisionKind.SelectCards"/> のとき）。</summary>
    public CardSelectionPurpose? Purpose { get; init; }

    /// <summary>選択すべき最小枚数・宣言数。</summary>
    public int MinCount { get; init; }

    /// <summary>選択できる最大枚数・宣言数。</summary>
    public int MaxCount { get; init; }

    /// <summary>カードを渡す相手（交換・7渡し）。</summary>
    public int? TargetPlayerId { get; init; }

    /// <summary>関連する効果（革命選択時の革命種別など）。</summary>
    public SpecialEffectType? Effect { get; init; }

    /// <summary>パス（4止め・砂嵐の割り込みの見送りを含む）できるか。</summary>
    public bool CanPass { get; init; }
}

/// <summary>コマンドの実行結果。</summary>
/// <param name="Error">エラー種別（成功時 <see cref="CommandError.None"/>）。</param>
/// <param name="PlayerId">コマンドを発行したプレイヤー。</param>
/// <param name="PlayReject">不正な出し札の詳細理由。</param>
public sealed record CommandResult(CommandError Error, int? PlayerId = null, PlayRejectReason PlayReject = PlayRejectReason.None)
{
    /// <summary>即時に受理されたか。</summary>
    public bool IsAccepted => Error == CommandError.None;

    /// <summary>イベント処理中のため後で実行されるか。</summary>
    public bool IsDeferred => Error == CommandError.Deferred;
}

// ======================================================================
// 結果
// ======================================================================

/// <summary>1ラウンドにおける1プレイヤーの成績。</summary>
public sealed record RoundStanding(int PlayerId, int Position, PlayerRank Rank, int Points, FinishKind Kind);

/// <summary>ラウンドの結果。</summary>
public sealed record RoundResult(
    int RoundNumber,
    bool IsExtraRound,
    IReadOnlyList<RoundStanding> Standings,
    bool IsGekokujo,
    int? MiyakoOchiPlayerId);

/// <summary>ゲーム終了時の1プレイヤーの成績。</summary>
public sealed record GameStanding(int PlayerId, string Name, int TotalScore, int Place);

/// <summary>ゲームの最終結果。</summary>
public sealed record GameResult(
    IReadOnlyList<GameStanding> Standings,
    IReadOnlyList<int> WinnerIds,
    int RoundsPlayed,
    IReadOnlyList<RoundResult> Rounds);

// ======================================================================
// イベント（UI演出・NPC・通信・ログの共通ストリーム）
// ======================================================================

/// <summary>ゲームイベントの基底。<see cref="GameEngine.EventOccurred"/> で発行順に通知される。</summary>
public abstract record GameEvent
{
    /// <summary>ゲーム内の通し番号（0始まり）。通信同期・リプレイ用。</summary>
    public long Sequence { get; internal set; }
}

/// <summary>フェーズが変わった。</summary>
public sealed record PhaseChangedEvent(GamePhase Phase) : GameEvent;

/// <summary>ラウンドが始まった。</summary>
public sealed record RoundStartedEvent(int RoundNumber, bool IsExtraRound, IReadOnlyList<int> SeatOrder) : GameEvent;

/// <summary>配牌が完了した（キーはプレイヤーID、値は枚数）。</summary>
public sealed record HandsDealtEvent(IReadOnlyDictionary<int, int> HandCounts) : GameEvent;

/// <summary>判断が求められた。</summary>
public sealed record DecisionRequestedEvent(PendingDecision Decision) : GameEvent;

/// <summary>カードが場に出された。</summary>
public sealed record CardsPlayedEvent(int PlayerId, PlayJudgement Judgement, FieldState Field) : GameEvent;

/// <summary>パスした（<paramref name="IsFourStopDecline"/> が true なら割り込み（4止め・砂嵐）の見送り）。</summary>
public sealed record PlayerPassedEvent(int PlayerId, bool IsFourStopDecline) : GameEvent;

/// <summary>特殊効果が適用された（Applied=false は革命選択で見送った場合）。</summary>
public sealed record EffectAppliedEvent(int PlayerId, TriggeredEffect Effect, bool Applied, FieldState Field) : GameEvent;

/// <summary>スキップで手番を飛ばされた。</summary>
public sealed record PlayersSkippedEvent(int ByPlayerId, IReadOnlyList<int> SkippedPlayerIds) : GameEvent;

/// <summary>カードがプレイヤー間で移動した。</summary>
public sealed record CardsTransferredEvent(int FromPlayerId, int ToPlayerId, IReadOnlyList<Card> Cards, CardTransferReason Reason) : GameEvent;

/// <summary>12ボンバーで数字が宣言された（この後、宣言された数字のカードが全員の手札から捨てられる）。</summary>
public sealed record RanksDeclaredEvent(int PlayerId, IReadOnlyList<Rank> Ranks) : GameEvent;

/// <summary>カードが捨てられた。</summary>
public sealed record CardsDiscardedEvent(int PlayerId, IReadOnlyList<Card> Cards, CardDiscardReason Reason) : GameEvent;

/// <summary>場が流れた。</summary>
public sealed record FieldFlushedEvent(int? NextLeaderId, SpecialEffectType? Cause, FieldState Field) : GameEvent;

/// <summary>プレイヤーがラウンドを抜けた。</summary>
/// <param name="PlayerId">抜けたプレイヤー。</param>
/// <param name="Kind">抜け方。</param>
/// <param name="Place">この時点での順位（1始まり。反則・都落ちは下位から確定する。下剋上が起きるとラウンド結果で入れ替わる）。</param>
public sealed record PlayerFinishedEvent(int PlayerId, FinishKind Kind, int Place) : GameEvent;

/// <summary>ラウンドが終了した。</summary>
public sealed record RoundEndedEvent(RoundResult Result, bool IsGameOver) : GameEvent;

/// <summary>ゲームが終了した。</summary>
public sealed record GameEndedEvent(GameResult Result) : GameEvent;

/// <summary>遅延実行されたコマンドが拒否された。</summary>
public sealed record CommandRejectedEvent(CommandResult Result) : GameEvent;

/// <summary>プレイヤーの操作主体が変わった（切断時のNPC引き継ぎなど）。</summary>
public sealed record PlayerKindChangedEvent(int PlayerId, PlayerKind Kind) : GameEvent;

// ======================================================================
// 乱数源
// ======================================================================

/// <summary>乱数源。テストではシード固定・スクリプト化した実装を注入する。</summary>
public interface IRandomSource
{
    /// <summary>0 以上 <paramref name="maxExclusive"/> 未満の整数を返す。</summary>
    int Next(int maxExclusive);
}

/// <summary><see cref="Random"/> による乱数源。</summary>
public sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random;

    /// <summary>乱数源を生成する。</summary>
    /// <param name="seed">シード（null なら非決定的）。</param>
    public SystemRandomSource(int? seed = null)
    {
        _random = seed is { } s ? new Random(s) : new Random();
    }

    /// <inheritdoc />
    public int Next(int maxExclusive) => _random.Next(maxExclusive);
}

/// <summary>乱数源の拡張メソッド。</summary>
public static class RandomSourceExtensions
{
    /// <summary>Fisher–Yates 法でリストをその場でシャッフルする。</summary>
    public static void Shuffle<T>(this IRandomSource random, IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
