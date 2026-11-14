using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;

namespace DTTS.Core.Models;

/// <summary>
/// 場に出された1手の記録。
/// </summary>
/// <param name="PlayerId">出したプレイヤーの座席ID。</param>
/// <param name="Play">出し札。</param>
/// <param name="TurnNumber">ラウンド内の通し手番番号。</param>
public sealed record PlayRecord(int PlayerId, CardPlay Play, int TurnNumber);

/// <summary>
/// パスの記録。
/// </summary>
/// <param name="PlayerId">パスしたプレイヤーの座席ID。</param>
/// <param name="TurnNumber">ラウンド内の通し手番番号。</param>
public sealed record PassRecord(int PlayerId, int TurnNumber);

/// <summary>
/// 縛りの状態。不変オブジェクト。
/// </summary>
public sealed record LockState
{
    /// <summary>縛りなしの初期状態。</summary>
    public static LockState None { get; } = new();

    /// <summary>
    /// 縛られているスート構成（枚数分）。<see cref="Suit.Joker"/> は任意スート（ワイルド）枠。
    /// 空ならスート縛りなし。
    /// </summary>
    public IReadOnlyList<Suit> LockedSuits { get; init; } = Array.Empty<Suit>();

    /// <summary>数しばが成立しているか。</summary>
    public bool IsNumberLocked { get; init; }

    /// <summary>直前までのスート一致の連続回数（縛り発生回数の判定用）。</summary>
    public int SuitMatchStreak { get; init; }

    /// <summary>直前までの連番（＋1）の連続回数。</summary>
    public int NumberMatchStreak { get; init; }

    /// <summary>スート縛りが成立しているか。</summary>
    public bool IsSuitLocked => LockedSuits.Count > 0;

    /// <summary>何らかの縛りが成立しているか。</summary>
    public bool IsAnyLocked => IsSuitLocked || IsNumberLocked;

    public override string ToString()
    {
        var parts = new List<string>();
        if (IsSuitLocked)
        {
            parts.Add("スート縛り[" + string.Join("", LockedSuits.Select(Card.SuitSymbol)) + "]");
        }
        if (IsNumberLocked)
        {
            parts.Add("数しば");
        }
        return parts.Count == 0 ? "縛りなし" : string.Join(" + ", parts);
    }
}

/// <summary>
/// 場の状態（現在の場札・縛り・革命・11バック・進行方向・パス履歴など）を表す不変オブジェクト。
/// <para>
/// 状態遷移は <c>With～</c> 系メソッドで新しいインスタンスを返す。
/// 判定（JudgeLogic）は本クラスを読み取るだけで変更しない。どの遷移をいつ適用するかは GameEngine が決める。
/// </para>
/// </summary>
public sealed record FieldState
{
    /// <summary>ラウンド開始時の初期状態を生成する。</summary>
    /// <param name="direction">初期の進行方向。</param>
    /// <param name="requiredCard">初手に必須のカード（ダイヤ3初手ルール時の♦3など）。</param>
    public static FieldState CreateInitial(PlayDirection direction = PlayDirection.Clockwise, Card? requiredCard = null)
        => new() { Direction = direction, RequiredCard = requiredCard };

    // ------------------------------------------------------------------
    // 場札
    // ------------------------------------------------------------------

    /// <summary>現在場に出ている（最後に出された）札。場が空なら null。</summary>
    public CardPlay? CurrentPlay { get; init; }

    /// <summary>現在の場札を出したプレイヤー。</summary>
    public int? LastPlayerId { get; init; }

    /// <summary>この場（流れるまで）に出された札の履歴。</summary>
    public IReadOnlyList<PlayRecord> PlayHistory { get; init; } = Array.Empty<PlayRecord>();

    /// <summary>この場（流れるまで）のパス履歴。</summary>
    public IReadOnlyList<PassRecord> PassHistory { get; init; } = Array.Empty<PassRecord>();

    /// <summary>場が空（親の出し待ち）か。</summary>
    public bool IsEmpty => CurrentPlay is null;

    // ------------------------------------------------------------------
    // 強さ反転
    // ------------------------------------------------------------------

    /// <summary>革命中か。</summary>
    public bool IsRevolution { get; init; }

    /// <summary>オーメン等により、以後の革命が封じられているか。</summary>
    public bool IsRevolutionLocked { get; init; }

    /// <summary>11バック中か。</summary>
    public bool IsElevenBack { get; init; }

    /// <summary>11バックが場流れ後も持続するか（強化11バック）。</summary>
    public bool IsElevenBackPersistent { get; init; }

    /// <summary>強さが反転しているか（革命 XOR 11バック）。</summary>
    public bool IsStrengthReversed => IsRevolution ^ IsElevenBack;

    // ------------------------------------------------------------------
    // 縛り・進行
    // ------------------------------------------------------------------

    /// <summary>縛りの状態。</summary>
    public LockState Lock { get; init; } = LockState.None;

    /// <summary>手番の進行方向。</summary>
    public PlayDirection Direction { get; init; } = PlayDirection.Clockwise;

    /// <summary>8切り発生直後で、4止めの割り込みを受付中か。</summary>
    public bool IsEightCutPending { get; init; }

    /// <summary>ろくろ首・99車の発生直後で、砂嵐の割り込みを受付中か。</summary>
    public bool IsSandstormStopPending { get; init; }

    /// <summary>次の出し札に必ず含めなければならないカード（ダイヤ3初手など）。不要なら null。</summary>
    public Card? RequiredCard { get; init; }

    /// <summary>ラウンド内の通し手番番号（出し・パスごとに増加）。</summary>
    public int TurnNumber { get; init; }

    /// <summary>ラウンド内で場が流れた回数（＝現在の場の通し番号）。</summary>
    public int TrickNumber { get; init; }

    /// <summary>この場でパスしたプレイヤーID集合。</summary>
    public IReadOnlyCollection<int> PassedPlayerIds => PassHistory.Select(p => p.PlayerId).Distinct().ToArray();

    /// <summary>指定プレイヤーがこの場でパス済みか。</summary>
    public bool HasPassed(int playerId) => PassHistory.Any(p => p.PlayerId == playerId);

    // ------------------------------------------------------------------
    // 状態遷移（純粋関数）
    // ------------------------------------------------------------------

    /// <summary>
    /// 出し札を場に置いた状態を返す。縛り状態は JudgeLogic が算出した値を渡す。
    /// 必須カード指定（ダイヤ3初手）は解除される。
    /// </summary>
    public FieldState WithPlay(int playerId, CardPlay play, LockState nextLock)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(nextLock);
        var history = new List<PlayRecord>(PlayHistory) { new(playerId, play, TurnNumber) };
        return this with
        {
            CurrentPlay = play,
            LastPlayerId = playerId,
            PlayHistory = history,
            Lock = nextLock,
            RequiredCard = null,
            IsEightCutPending = false,
            IsSandstormStopPending = false,
            TurnNumber = TurnNumber + 1,
        };
    }

    /// <summary>パスを記録した状態を返す。</summary>
    public FieldState WithPass(int playerId)
    {
        var passes = new List<PassRecord>(PassHistory) { new(playerId, TurnNumber) };
        return this with { PassHistory = passes, TurnNumber = TurnNumber + 1 };
    }

    /// <summary>
    /// 革命状態を反転した状態を返す。
    /// </summary>
    /// <param name="lockFurtherRevolutions">以後の革命を封じるか（オーメン）。</param>
    public FieldState WithRevolutionToggled(bool lockFurtherRevolutions = false)
        => this with
        {
            IsRevolution = !IsRevolution,
            IsRevolutionLocked = IsRevolutionLocked || lockFurtherRevolutions,
        };

    /// <summary>
    /// 11バックを反転した状態を返す（11バック中にさらに11バックが出た場合は解除）。
    /// </summary>
    /// <param name="persistent">強化11バック（場流れ後も持続）として扱うか。</param>
    public FieldState WithElevenBackToggled(bool persistent)
    {
        bool next = !IsElevenBack;
        return this with { IsElevenBack = next, IsElevenBackPersistent = next && persistent };
    }

    /// <summary>進行方向を反転した状態を返す。</summary>
    public FieldState WithDirectionReversed()
        => this with
        {
            Direction = Direction == PlayDirection.Clockwise ? PlayDirection.CounterClockwise : PlayDirection.Clockwise,
        };

    /// <summary>8切りの4止め受付状態を設定した状態を返す。</summary>
    public FieldState WithEightCutPending(bool pending) => this with { IsEightCutPending = pending };

    /// <summary>ろくろ首・99車への砂嵐の割り込み受付状態を設定した状態を返す。</summary>
    public FieldState WithSandstormStopPending(bool pending) => this with { IsSandstormStopPending = pending };

    /// <summary>
    /// 場を流した状態を返す。場札・履歴・縛りはリセットされ、
    /// 11バックは強化11バック（持続）でなければ解除される。革命状態は維持される。
    /// </summary>
    public FieldState Flushed()
        => this with
        {
            CurrentPlay = null,
            LastPlayerId = null,
            PlayHistory = Array.Empty<PlayRecord>(),
            PassHistory = Array.Empty<PassRecord>(),
            Lock = LockState.None,
            IsElevenBack = IsElevenBack && IsElevenBackPersistent,
            IsElevenBackPersistent = IsElevenBack && IsElevenBackPersistent,
            IsEightCutPending = false,
            IsSandstormStopPending = false,
            TrickNumber = TrickNumber + 1,
        };

    public override string ToString()
    {
        string play = CurrentPlay?.ToString() ?? "(空)";
        string rev = IsRevolution ? " 革命中" : string.Empty;
        string eb = IsElevenBack ? (IsElevenBackPersistent ? " 強化11バック中" : " 11バック中") : string.Empty;
        return $"場:{play}{rev}{eb} {Lock} 方向:{Direction}";
    }
}
