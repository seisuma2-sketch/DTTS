using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Core.Logic;

/// <summary>
/// 出し札によって発動した特殊効果1件。
/// </summary>
/// <param name="Type">効果の種類。</param>
/// <param name="Magnitude">効果量（スキップ人数・渡す枚数・捨てる枚数・宣言数など。該当しない効果は1）。</param>
/// <param name="IsOptional">プレイヤーが発動を選択できる効果か（革命選択ルール等）。</param>
public sealed record TriggeredEffect(SpecialEffectType Type, int Magnitude = 1, bool IsOptional = false)
{
    /// <summary>この効果が場を流すか。</summary>
    public bool FlushesField => Type.FlushesField();

    /// <summary>この効果が革命（強さ反転）を引き起こすか。</summary>
    public bool IsRevolutionTrigger => Type.IsRevolutionTrigger();
}

/// <summary>
/// 出し札の判定結果。合法・不正の別、採用された解釈、発動効果、次の縛り状態、禁止あがり判定を含む。
/// </summary>
public sealed class PlayJudgement
{
    private PlayJudgement(
        bool isLegal,
        PlayRejectReason rejectReason,
        CardPlay? play,
        BeatMethod beatMethod,
        IReadOnlyList<CardPlay> alternatives,
        IReadOnlyList<TriggeredEffect> effects,
        LockState nextLock,
        ForbiddenFinishReason forbiddenFinish)
    {
        IsLegal = isLegal;
        RejectReason = rejectReason;
        Play = play;
        BeatMethod = beatMethod;
        Alternatives = alternatives;
        Effects = effects;
        NextLock = nextLock;
        ForbiddenFinish = forbiddenFinish;
    }

    /// <summary>合法手か。</summary>
    public bool IsLegal { get; }

    /// <summary>不正な場合の理由。合法なら <see cref="PlayRejectReason.None"/>。</summary>
    public PlayRejectReason RejectReason { get; }

    /// <summary>
    /// 採用された解釈。合法時は必ず非null。
    /// 不正時は（解析可能であれば）代表的な解釈が入る場合がある。
    /// </summary>
    public CardPlay? Play { get; }

    /// <summary>どの根拠で場に勝ったか。</summary>
    public BeatMethod BeatMethod { get; }

    /// <summary>合法な全解釈（<see cref="Play"/> を含む）。UIでジョーカーの役割を選ばせる場合に使用する。</summary>
    public IReadOnlyList<CardPlay> Alternatives { get; }

    /// <summary>発動する特殊効果（発動順＝推奨適用順）。</summary>
    public IReadOnlyList<TriggeredEffect> Effects { get; }

    /// <summary>この札を場に置いた後の縛り状態。</summary>
    public LockState NextLock { get; }

    /// <summary>あがり手として出した場合の禁止あがり該当理由。</summary>
    public ForbiddenFinishReason ForbiddenFinish { get; }

    /// <summary>禁止あがりに該当するか。</summary>
    public bool IsForbiddenFinish => ForbiddenFinish != ForbiddenFinishReason.None;

    /// <summary>いずれかの効果で場が流れるか。</summary>
    public bool FlushesField => Effects.Any(e => e.FlushesField);

    /// <summary>革命（強さ反転）が発生するか。</summary>
    public bool TriggersRevolution => Effects.Any(e => e.IsRevolutionTrigger);

    /// <summary>指定の効果を含むか。</summary>
    public bool HasEffect(SpecialEffectType type) => Effects.Any(e => e.Type == type);

    /// <summary>指定の効果を取得する（なければ null）。</summary>
    public TriggeredEffect? GetEffect(SpecialEffectType type) => Effects.FirstOrDefault(e => e.Type == type);

    internal static PlayJudgement Legal(
        CardPlay play,
        BeatMethod beatMethod,
        IReadOnlyList<CardPlay> alternatives,
        IReadOnlyList<TriggeredEffect> effects,
        LockState nextLock,
        ForbiddenFinishReason forbiddenFinish)
        => new(true, PlayRejectReason.None, play, beatMethod, alternatives, effects, nextLock, forbiddenFinish);

    internal static PlayJudgement Reject(
        PlayRejectReason reason,
        CardPlay? play = null,
        ForbiddenFinishReason forbiddenFinish = ForbiddenFinishReason.None)
        => new(false, reason, play, BeatMethod.Lead, Array.Empty<CardPlay>(), Array.Empty<TriggeredEffect>(), LockState.None, forbiddenFinish);

    public override string ToString()
        => IsLegal
            ? $"合法 {Play} ({BeatMethod}) 効果:[{string.Join(",", Effects.Select(e => e.Type))}] {NextLock}"
            : $"不正 ({RejectReason}) {Play}";
}
