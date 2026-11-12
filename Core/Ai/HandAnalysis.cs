using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Core.Ai;

/// <summary>
/// 手札の構造分析（ペア・階段の検出、必要手数の見積もり、手札価値の評価）。
/// NPCの評価関数から利用する純粋関数群。
/// </summary>
public static class HandAnalysis
{
    /// <summary>
    /// 手札を出し切るのに必要なおおよその手数を見積もる。
    /// 同じ数字は1手、単独の数字が同スートで連続していれば階段として1手にまとめる。ジョーカーは他の組に付けられるため0手と数える。
    /// </summary>
    public static int EstimateTurns(IReadOnlyCollection<Card> cards, RuleSettings rules)
    {
        if (cards.Count == 0)
        {
            return 0;
        }
        var naturals = cards.Where(c => !c.IsJoker).ToList();
        if (naturals.Count == 0)
        {
            return 1;
        }
        int turns = naturals.Select(c => c.Rank).Distinct().Count();
        foreach (var run in FindSequenceRuns(naturals, rules))
        {
            turns -= run.Count - 1;
        }
        return Math.Max(turns, 1);
    }

    /// <summary>
    /// 階段として出せる「単独の数字」のカード列（同スート・連番・最小枚数以上）を検出する。
    /// ペア以上の数字は崩さない前提で対象外とする。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Card>> FindSequenceRuns(IReadOnlyCollection<Card> cards, RuleSettings rules)
    {
        var result = new List<IReadOnlyList<Card>>();
        if (!rules.Sequence.Enabled)
        {
            return result;
        }
        var rankCounts = cards.Where(c => !c.IsJoker).GroupBy(c => c.Rank).ToDictionary(g => g.Key, g => g.Count());
        int maxTop = rules.Sequence.AllowTwoInSequence ? Card.MaxNaturalStrength : Card.MaxNaturalStrength - 1;

        foreach (var suitGroup in cards.Where(c => !c.IsJoker && rankCounts[c.Rank] == 1).GroupBy(c => c.Suit))
        {
            var sorted = suitGroup.Where(c => c.BaseStrength <= maxTop).OrderBy(c => c.BaseStrength).ToList();
            var run = new List<Card>();
            foreach (var card in sorted)
            {
                if (run.Count > 0 && card.BaseStrength != run[^1].BaseStrength + 1)
                {
                    if (run.Count >= rules.Sequence.MinLength)
                    {
                        result.Add(run);
                    }
                    run = new List<Card>();
                }
                run.Add(card);
            }
            if (run.Count >= rules.Sequence.MinLength)
            {
                result.Add(run);
            }
        }
        return result;
    }

    /// <summary>
    /// 手札の価値（高いほど勝ちやすい）。強いカードほど非線形に価値が高く、必要手数が多いほど低い。
    /// </summary>
    /// <param name="cards">手札。</param>
    /// <param name="reversed">強さが反転している前提で評価するか。</param>
    /// <param name="rules">ルール設定。</param>
    public static double HandValue(IReadOnlyCollection<Card> cards, bool reversed, RuleSettings rules)
    {
        double value = 0;
        foreach (var card in cards)
        {
            double s = NormalizedStrength(card, reversed);
            value += s * s * 10.0;
        }
        return value - (EstimateTurns(cards, rules) * 2.0);
    }

    /// <summary>カードの強さを 0（最弱）〜1（ジョーカー）に正規化する。</summary>
    public static double NormalizedStrength(Card card, bool reversed)
        => (card.GetStrength(reversed) - 1) / (double)(Card.JokerStrength - 1);

    /// <summary>数字の強さを 0〜1 に正規化する。</summary>
    public static double NormalizedStrength(Rank rank, bool reversed)
        => (Card.GetStrength(rank, reversed) - 1) / (double)(Card.JokerStrength - 1);

    /// <summary>
    /// 残り手札が「あがり手として出すと禁止あがりになるカード」だけで構成されているか（少枚数時の詰み回避用）。
    /// </summary>
    public static bool IsForbiddenOnly(IReadOnlyCollection<Card> cards, FieldState field, RuleSettings rules)
    {
        var f = rules.ForbiddenFinish;
        if (!f.Enabled || cards.Count == 0)
        {
            return false;
        }
        bool reversed = f.StrongestFollowsReversal && field.IsStrengthReversed;
        Rank strongest = Card.GetRankFromBaseStrength(reversed ? Card.MinStrength : Card.MaxNaturalStrength);
        return cards.All(c =>
            (c.IsJoker && f.ForbidJoker)
            || (!c.IsJoker && f.ForbidStrongestNumber && c.Rank == strongest)
            || (!c.IsJoker && f.ForbidEightCut && rules.Flow.EightCutEnabled && c.Rank == rules.Flow.EightCutRank)
            || (!c.IsJoker && f.ForbidElevenBack && rules.ElevenBack.Enabled && c.Rank == rules.ElevenBack.TriggerRank)
            || (!c.IsJoker && f.ForbidTwelveBomber && rules.Special.TwelveBomberEnabled && c.Rank == rules.Special.TwelveBomberRank));
    }

    /// <summary>
    /// 出し札が手札の構成（ペア・スリーカード・階段）をいくつ崩すかを数える。
    /// </summary>
    /// <param name="play">出し札。</param>
    /// <param name="hand">出す前の手札。</param>
    /// <param name="rules">ルール設定。</param>
    /// <param name="largeGroupBreaks">崩した組のうち、革命の材料になり得る大きな組（革命枚数−1 以上）の数。</param>
    public static int CountBreaks(CardPlay play, IReadOnlyCollection<Card> hand, RuleSettings rules, out int largeGroupBreaks)
    {
        largeGroupBreaks = 0;
        int breaks = 0;
        var rankCounts = hand.Where(c => !c.IsJoker).GroupBy(c => c.Rank).ToDictionary(g => g.Key, g => g.Count());
        int largeThreshold = Math.Max(2, rules.Revolution.MinCards - 1);

        foreach (var group in play.NaturalCards.GroupBy(c => c.Rank))
        {
            int inHand = rankCounts[group.Key];
            int used = group.Count();
            if (used < inHand)
            {
                breaks++;
                if (inHand >= largeThreshold)
                {
                    largeGroupBreaks++;
                }
            }
        }

        if (!play.IsSequenceLike)
        {
            var runs = FindSequenceRuns(hand, rules);
            var used = new HashSet<int>(play.Cards.Select(c => c.Id));
            foreach (var run in runs)
            {
                int taken = run.Count(c => used.Contains(c.Id));
                if (taken > 0 && taken < run.Count)
                {
                    breaks++;
                }
            }
        }
        return breaks;
    }
}
