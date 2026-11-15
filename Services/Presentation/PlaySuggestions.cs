using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Models;

namespace DTTS.Services.Presentation;

/// <summary>手札の上に並べる「まとめて選ぶ」候補（ペア・3枚・階段など、今の場に出せる2枚以上の組）。</summary>
/// <param name="Label">ボタンの文字（例：「5 ×2」「♠5-6-7」）。</param>
/// <param name="Cards">押したときに選ぶカード。</param>
public sealed record PlaySuggestion(string Label, IReadOnlyList<Card> Cards);

/// <summary>
/// 合法手から「まとめて選ぶ」候補を作る（1人用・ホストはエンジンの合法手、ゲストはホストから届いた合法手を使う）。
/// <list type="bullet">
/// <item>1枚出しは手札をクリックすれば済むので候補にしない。</item>
/// <item>同じ数字の組は「数字×枚数」ごとに1つ（ジョーカーの少ない組み合わせを優先）。</item>
/// <item>弱い順に並べ、多すぎるときは先頭から <paramref name="max"/> 個まで。</item>
/// </list>
/// </summary>
public static class PlaySuggestions
{
    /// <summary>候補の最大数（手札の上の1行に収まる程度）。</summary>
    public const int DefaultMax = 6;

    /// <summary>合法手（カードの組）から候補を作る。</summary>
    public static IReadOnlyList<PlaySuggestion> Build(IEnumerable<IReadOnlyList<Card>> legalPlays, int max = DefaultMax)
    {
        ArgumentNullException.ThrowIfNull(legalPlays);
        var best = new Dictionary<string, (IReadOnlyList<Card> Cards, int Jokers, int Strength, bool IsGroup)>(StringComparer.Ordinal);
        foreach (var play in legalPlays)
        {
            if (play is null || play.Count < 2)
            {
                continue;
            }
            var naturals = play.Where(c => !c.IsJoker).ToList();
            if (naturals.Count == 0)
            {
                continue; // ジョーカーだけの組は候補にしない（手で選ぶ）
            }
            int jokers = play.Count - naturals.Count;
            bool isGroup = naturals.Select(c => c.Rank).Distinct().Count() == 1;
            int strength = naturals.Min(c => Card.GetBaseStrength(c.Rank));
            // 同じ数字の組は「数字×枚数」で、階段は自然札の組で同一視する（ジョーカーの使い方だけが違う組を1つにまとめる）
            string key = isGroup
                ? $"G:{naturals[0].Rank}:{play.Count}"
                : "S:" + string.Join(",", naturals.OrderBy(c => c.Id).Select(c => c.Id)) + ":" + play.Count;
            if (!best.TryGetValue(key, out var current) || jokers < current.Jokers)
            {
                best[key] = (play, jokers, strength, isGroup);
            }
        }

        return best.Values
            .OrderBy(v => v.Strength)
            .ThenBy(v => v.IsGroup ? 0 : 1)
            .ThenBy(v => v.Cards.Count)
            .ThenBy(v => v.Jokers)
            .Take(Math.Max(0, max))
            .Select(v => new PlaySuggestion(Label(v.Cards, v.IsGroup), v.Cards))
            .ToList();
    }

    /// <summary>候補の表示名。同じ数字の組は「5 ×2」、階段は「♠5-6-7」。ジョーカー入りは「+JK」を付ける。</summary>
    public static string Label(IReadOnlyList<Card> cards, bool isGroup)
    {
        var naturals = cards.Where(c => !c.IsJoker).ToList();
        int jokers = cards.Count - naturals.Count;
        string jk = jokers > 0 ? (jokers == 1 ? "+JK" : $"+JK×{jokers}") : "";
        if (isGroup)
        {
            return $"{Card.RankCode(naturals[0].Rank)} ×{cards.Count}{jk}";
        }
        var ordered = naturals.OrderBy(c => Card.GetBaseStrength(c.Rank)).ToList();
        var suits = ordered.Select(c => c.Suit).Distinct().ToList();
        string suit = suits.Count == 1 ? Card.SuitSymbol(suits[0]) : "";
        return suit + string.Join("-", ordered.Select(c => (suits.Count == 1 ? "" : Card.SuitSymbol(c.Suit)) + Card.RankCode(c.Rank))) + jk;
    }

    /// <summary>手札から、指定のカードと同じ数字のカードをすべて返す（右クリックでの「同じ数字をまとめて選ぶ」用）。</summary>
    public static IReadOnlyList<Card> SameRank(IEnumerable<Card> hand, Card card)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(card);
        return card.IsJoker
            ? hand.Where(c => c.IsJoker).ToList()
            : hand.Where(c => !c.IsJoker && c.Rank == card.Rank).ToList();
    }
}
