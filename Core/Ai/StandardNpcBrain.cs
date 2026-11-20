using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

namespace DTTS.Core.Ai;

/// <summary>
/// 標準のNPC思考エンジン。合法手ごとに評価値を計算し、最も高い手（またはパス）を選ぶ。
/// <para>評価の主な要素：</para>
/// <list type="bullet">
/// <item>手札を減らす価値（多く出すほど高い）</item>
/// <item>強いカード・ジョーカーを消費するコスト（性格で重みが変わり、終盤ほど小さくなる）</item>
/// <item>ペア・階段を崩すペナルティ</item>
/// <item>あがり（禁止あがりは大幅減点）、次の1手であがれる見込み、禁止カードだけが残る事態の回避</item>
/// <item>革命による手札価値の変化、場流しによる親の獲得、7渡し・10捨て・スキップ等の効果</item>
/// </list>
/// 強さ（難易度）はノイズ・ランダム手の確率・考慮範囲（構成・禁止あがり・カード記憶）で調整する。
/// </summary>
public sealed class StandardNpcBrain : INpcStrategy
{
    private const double FinishBonus = 1000.0;
    private const double FoulPenalty = -1000.0;
    private const double NearFinishBonus = 300.0;

    private readonly IRandomSource _random;
    private readonly NpcWeights _w;

    // 1回の判断の中だけで使う計算結果のキャッシュ（DecidePlay の開始時に破棄）
    private readonly Dictionary<string, bool> _leadMemo = new(StringComparer.Ordinal);
    private IReadOnlyList<Card>? _unseenCache;
    private int? _turnsBeforeCache;
    private NpcContext? _cacheOwner;

    /// <summary>思考エンジンを生成する。</summary>
    /// <param name="profile">強さ・性格。</param>
    /// <param name="random">乱数源（テスト時はシード固定）。</param>
    public StandardNpcBrain(NpcProfile profile, IRandomSource? random = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _w = profile.Weights;
        _random = random ?? new SystemRandomSource();
    }

    /// <inheritdoc />
    public NpcProfile Profile { get; }

    // ======================================================================
    // 出し札の判断
    // ======================================================================

    /// <inheritdoc />
    public NpcPlayDecision DecidePlay(NpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureCacheFor(context);
        var legal = context.LegalPlays;
        bool canPass = context.Decision.CanPass;
        if (legal.Count == 0)
        {
            return NpcPlayDecision.Pass;
        }

        // よわい：一定確率で完全にランダムな手
        if (_w.RandomMovePercent > 0 && _random.Next(100) < _w.RandomMovePercent)
        {
            if (canPass && _random.Next(3) == 0)
            {
                return NpcPlayDecision.Pass;
            }
            return new NpcPlayDecision(legal[_random.Next(legal.Count)].Play!.Cards);
        }

        PlayJudgement? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var judgement in legal)
        {
            double score = ScorePlay(judgement, context) + Noise();
            if (score > bestScore)
            {
                bestScore = score;
                best = judgement;
            }
        }

        if (canPass && ScorePass(context) + Noise() > bestScore)
        {
            return NpcPlayDecision.Pass;
        }
        return new NpcPlayDecision(best!.Play!.Cards);
    }

    /// <summary>
    /// 合法手の評価値を計算する（ノイズなし）。テスト・デバッグ表示用に公開している。
    /// </summary>
    public double ScorePlay(PlayJudgement judgement, NpcContext context)
    {
        ArgumentNullException.ThrowIfNull(judgement);
        ArgumentNullException.ThrowIfNull(context);
        EnsureCacheFor(context);
        var play = judgement.Play!;
        var rules = context.Rules;
        bool reversed = context.Field.IsStrengthReversed;
        var usedIds = new HashSet<int>(play.Cards.Select(c => c.Id));
        var remaining = context.Hand.Where(c => !usedIds.Contains(c.Id)).ToList();

        // --- あがり ---
        if (remaining.Count == 0)
        {
            bool foul = judgement.IsForbiddenFinish && rules.ForbiddenFinish.Penalty == ForbiddenFinishPenalty.LastPlace;
            return foul ? FoulPenalty : FinishBonus;
        }

        double score = _w.CardShed * play.Count;

        // --- 強いカードを使うコスト（終盤・相手のあがり間近では小さくする） ---
        double endgame = 1.0;
        if (remaining.Count <= 3)
        {
            endgame *= 0.5;
        }
        if (context.MinOpponentHandCount <= 2)
        {
            endgame *= 0.4;
        }
        double cost = 0;
        foreach (var card in play.Cards)
        {
            double s = HandAnalysis.NormalizedStrength(card, reversed);
            cost += s * s * 8.0 * _w.StrongCardCost;
            if (card.IsJoker)
            {
                cost += 6.0 * _w.JokerHold;
            }
        }
        score -= cost * endgame;

        // --- 手札の構成を崩すペナルティ ---
        if (_w.ConsidersStructure)
        {
            int breaks = HandAnalysis.CountBreaks(play, context.Hand, rules, out int largeBreaks);
            score -= breaks * _w.BreakPenalty;
            score -= largeBreaks * 6.0 * (_w.Revolution - 1.0); // 革命狙いは革命の材料を崩したがらない
            int turnsBefore = _turnsBeforeCache ??= HandAnalysis.EstimateTurns(context.Hand, rules);
            int turnsAfter = HandAnalysis.EstimateTurns(remaining, rules);
            score += (turnsBefore - turnsAfter) * 1.5;
        }

        // --- 次の1手であがれる見込み ---
        bool keepsLead = judgement.FlushesField || (_w.CountsCards && remaining.Count <= 8 && IsUnbeatable(judgement, context));
        if (keepsLead)
        {
            score += _w.Flush * 2.0;
            if (_w.ConsidersStructure && CanFinishInOneLead(remaining, context))
            {
                score += NearFinishBonus;
            }
            else if (_w.CountsCards)
            {
                score += ControlPlanBonus(remaining, context);
            }
        }
        if (judgement.BeatMethod == BeatMethod.FourStop)
        {
            score += 4.0; // 相手の8切りを止める
        }

        // --- 禁止カードだけが残る事態を避ける ---
        if (_w.AvoidsForbiddenFinish && remaining.Count <= 2 && HandAnalysis.IsForbiddenOnly(remaining, context.Field, rules))
        {
            score -= 50.0;
        }

        // --- 革命 ---
        if (judgement.TriggersRevolution && !judgement.Effects.Any(e => e.IsOptional))
        {
            double delta = HandAnalysis.HandValue(remaining, !reversed, rules) - HandAnalysis.HandValue(remaining, reversed, rules);
            score += delta * 1.5 * _w.Revolution;
        }
        else if (judgement.TriggersRevolution)
        {
            score += Math.Max(0, RevolutionGain(remaining, context)) * 1.5 * _w.Revolution; // 選択式なら損はしない
        }

        // --- 11バック（場が流れるまで反転）---
        // 「次の1手番だけ」の設定では反転の中で出すのは次の人だけなので、自分の手札にとっての損得はない
        if ((judgement.HasEffect(SpecialEffectType.ElevenBack) || judgement.HasEffect(SpecialEffectType.StrongElevenBack))
            && rules.ElevenBack.Duration != ElevenBackDuration.NextTurnOnly)
        {
            double delta = HandAnalysis.HandValue(remaining, !reversed, rules) - HandAnalysis.HandValue(remaining, reversed, rules);
            score += delta * 0.3;
        }

        // --- その他の効果 ---
        foreach (var effect in judgement.Effects)
        {
            score += effect.Type switch
            {
                SpecialEffectType.SevenPass or SpecialEffectType.TenDiscard => 1.5 * Math.Min(effect.Magnitude, remaining.Count),
                SpecialEffectType.TwelveBomber => 2.0,
                SpecialEffectType.FiveSkip or SpecialEffectType.ThirteenSkip => 0.5 * effect.Magnitude,
                SpecialEffectType.SuitLock => LockBonus(judgement.NextLock, remaining),
                _ => 0.0,
            };
        }

        return score;
    }

    /// <summary>パスの評価値（ノイズなし）。</summary>
    public double ScorePass(NpcContext context)
    {
        double score = _w.PassBaseline;
        if (context.MinOpponentHandCount <= 2)
        {
            score -= 5.0; // 相手があがりそうなら止めに行く
        }
        if (context.Decision.Kind.IsInterruptChance())
        {
            score += 2.0; // 割り込みは強制ではない
        }
        return score;
    }

    private double Noise() => _w.Noise <= 0 ? 0 : ((_random.Next(2001) / 1000.0) - 1.0) * _w.Noise;

    private static double LockBonus(LockState nextLock, IReadOnlyCollection<Card> remaining)
    {
        if (!nextLock.IsSuitLocked)
        {
            return 0;
        }
        var suits = new HashSet<Suit>(nextLock.LockedSuits.Where(s => s != Suit.Joker));
        int matching = remaining.Count(c => c.IsJoker || suits.Contains(c.Suit));
        return matching >= 2 ? 1.0 : -0.5;
    }

    private static double RevolutionGain(IReadOnlyCollection<Card> cards, NpcContext context)
    {
        bool reversed = context.Field.IsStrengthReversed;
        return HandAnalysis.HandValue(cards, !reversed, context.Rules) - HandAnalysis.HandValue(cards, reversed, context.Rules);
    }

    /// <summary>残り手札が1手で出し切れる組み合わせで、かつ禁止あがりにならないか。</summary>
    private static bool CanFinishInOneLead(IReadOnlyList<Card> remaining, NpcContext context)
    {
        var lead = context.Field.Flushed();
        var judgement = context.Judge.Judge(remaining, lead, isFinishingPlay: true);
        return judgement.IsLegal && !judgement.IsForbiddenFinish;
    }

    /// <summary>
    /// 所在不明のカードをすべて1人の相手が持っていると仮定しても返されない手か（つよい専用のカード記憶）。
    /// </summary>
    private bool IsUnbeatable(PlayJudgement judgement, NpcContext context)
    {
        if (judgement.FlushesField)
        {
            return true;
        }
        var after = context.Field.WithPlay(context.PlayerId, judgement.Play!, judgement.NextLock);
        var unseen = Unseen(context);
        if (unseen.Count == 0)
        {
            return true;
        }
        return context.Judge.GetLegalPlays(unseen, after).Count == 0;
    }

    /// <summary>
    /// 勝ち筋の読み（つよい専用）：残り手札を組に分け、「返されない組」を順に出していけば
    /// 最後の1組であがれる状態なら加点する。返されない組を先に出す手順が自然と選ばれる。
    /// </summary>
    private double ControlPlanBonus(IReadOnlyList<Card> remaining, NpcContext context)
    {
        if (remaining.Count == 0 || remaining.Count > 8)
        {
            return 0;
        }
        var groups = DecomposeForPlan(remaining, context.Rules);
        int boss = groups.Count(g => IsUnbeatableLead(g, context));
        return groups.Count - boss <= 1 ? 150.0 : 0.0;
    }

    /// <summary>手札を「出す単位」に分ける（階段 → 同じ数字の組 → ジョーカーは単騎）。</summary>
    private static List<IReadOnlyList<Card>> DecomposeForPlan(IReadOnlyList<Card> cards, RuleSettings rules)
    {
        var groups = new List<IReadOnlyList<Card>>();
        var runs = HandAnalysis.FindSequenceRuns(cards, rules);
        var inRun = new HashSet<int>(runs.SelectMany(r => r).Select(c => c.Id));
        groups.AddRange(runs);
        groups.AddRange(cards.Where(c => !c.IsJoker && !inRun.Contains(c.Id))
            .GroupBy(c => c.Rank)
            .Select(g => (IReadOnlyList<Card>)g.ToList()));
        groups.AddRange(cards.Where(c => c.IsJoker).Select(c => (IReadOnlyList<Card>)new[] { c }));
        return groups;
    }

    /// <summary>この組を親として出したとき、誰にも返されないか（結果はこの判断中だけ記憶する）。</summary>
    private bool IsUnbeatableLead(IReadOnlyList<Card> group, NpcContext context)
    {
        string key = string.Join(",", group.Select(c => c.Id).OrderBy(i => i));
        if (_leadMemo.TryGetValue(key, out bool cached))
        {
            return cached;
        }
        var lead = context.Field.Flushed();
        var judgement = context.Judge.Judge(group, lead);
        bool result = judgement.IsLegal && IsUnbeatable(judgement, new NpcContext(
            context.PlayerId, context.Hand, lead, context.Rules, context.Judge, context.Decision,
            context.LegalPlays, context.OpponentHandCounts, context.SeenCards, context.MyRank));
        _leadMemo[key] = result;
        return result;
    }

    private IReadOnlyList<Card> Unseen(NpcContext context)
        => _unseenCache ??= context.GetUnseenCards();

    /// <summary>判断材料が変わったらキャッシュを破棄する（ScorePlay を直接呼ばれた場合も含む）。</summary>
    private void EnsureCacheFor(NpcContext context)
    {
        if (ReferenceEquals(context, _cacheOwner))
        {
            return;
        }
        _cacheOwner = context;
        _leadMemo.Clear();
        _unseenCache = null;
        _turnsBeforeCache = null;
    }

    // ======================================================================
    // カード選択・数字宣言・革命選択
    // ======================================================================

    /// <inheritdoc />
    public IReadOnlyList<Card> SelectCards(NpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var d = context.Decision;
        int count = d.Purpose == CardSelectionPurpose.TenDiscard ? d.MaxCount : d.MinCount;
        count = Math.Clamp(count, d.MinCount, Math.Min(d.MaxCount, context.Hand.Count));
        if (count <= 0)
        {
            return Array.Empty<Card>();
        }

        // 価値の低いカードから手放す（交換・7渡し・10捨てすべて同じ方針）
        bool reversed = context.Field.IsStrengthReversed;
        var rankCounts = context.Hand.Where(c => !c.IsJoker).GroupBy(c => c.Rank).ToDictionary(g => g.Key, g => g.Count());
        var runMembers = new HashSet<int>(HandAnalysis.FindSequenceRuns(context.Hand, context.Rules).SelectMany(r => r).Select(c => c.Id));

        return context.Hand
            .Select(card => (card, keep: KeepValue(card, reversed, rankCounts, runMembers)))
            .OrderBy(x => x.keep)
            .ThenBy(x => x.card.Id)
            .Take(count)
            .Select(x => x.card)
            .ToList();
    }

    private double KeepValue(Card card, bool reversed, Dictionary<Rank, int> rankCounts, HashSet<int> runMembers)
    {
        double s = HandAnalysis.NormalizedStrength(card, reversed);
        double value = s * s * 10.0;
        if (card.IsJoker)
        {
            value += 100.0;
        }
        else if (_w.ConsidersStructure)
        {
            if (rankCounts[card.Rank] >= 2)
            {
                value += 3.0 * rankCounts[card.Rank] * (_w.Revolution > 1.0 ? 2.0 : 1.0);
            }
            if (runMembers.Contains(card.Id))
            {
                value += 2.0;
            }
        }
        return value + Noise();
    }

    /// <inheritdoc />
    public IReadOnlyList<Rank> DeclareRanks(NpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int count = Math.Clamp(context.Decision.MinCount, 1, GameEngine.MaxDeclarableRanks);
        bool reversed = context.Field.IsStrengthReversed;
        var unseen = _w.CountsCards ? context.GetUnseenCards() : null;

        // 全員が宣言した数字を捨てる：自分が多く持ち（特に弱いカード）、相手が少なく持つ数字ほど得
        var candidates = Enumerable.Range(1, Card.RanksPerSuit)
            .Select(i => (Rank)i)
            .Select(rank =>
            {
                int own = context.Hand.Count(c => !c.IsJoker && c.Rank == rank);
                double opponents = unseen is null ? 4.0 - own : unseen.Count(c => !c.IsJoker && c.Rank == rank);
                double weakness = own > 0 ? (1.0 - HandAnalysis.NormalizedStrength(rank, reversed)) * 2.0 : 0.0;
                return (rank, score: (own * 4.0) - (opponents * 1.5) + weakness + Noise());
            })
            .ToList();

        // ジョーカー：自分が持っていれば決して宣言しない。カードを記憶する思考（つよい）は、
        // 相手がまだ持っていると分かるときに最強札を消しに行く。それ以外は数字が足りないときの最後の候補
        bool ownJoker = context.Hand.Any(c => c.IsJoker);
        int jokersOut = unseen?.Count(c => c.IsJoker) ?? 0;
        double jokerScore = ownJoker ? double.NegativeInfinity : (jokersOut > 0 ? 5.0 * jokersOut : -100.0);
        candidates.Add((Rank.Joker, jokerScore));

        return candidates
            .OrderByDescending(x => x.score)
            .ThenBy(x => (int)x.rank)
            .Take(count)
            .Select(x => x.rank)
            .ToList();
    }

    /// <inheritdoc />
    public bool ChooseRevolution(NpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RevolutionGain(context.Hand, context) * _w.Revolution + _w.RevolutionBias + Noise() > 0;
    }
}
