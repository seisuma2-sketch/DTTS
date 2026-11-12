using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;

namespace DTTS.Core.Models;

/// <summary>
/// 解析済みの出し札（1つの「解釈」）。
/// <para>
/// 同じカード集合でもジョーカーの使い方により複数の解釈が成立し得る
/// （例: ♠5 + Joker×2 は「5のスリーカード」「♠3-4-5」「♠5-6-7」等）。
/// <c>JudgeLogic.Analyze</c> は全解釈を列挙し、各解釈を本クラスで表現する。
/// </para>
/// 不変オブジェクトであり、生成後に内容は変化しない。
/// </summary>
public sealed class CardPlay
{
    /// <summary>
    /// 出し札を生成する。通常は <c>JudgeLogic.Analyze</c> 経由で生成する。
    /// </summary>
    /// <param name="type">組み合わせ種別。</param>
    /// <param name="cards">実カード（階段は位置順＝基本強さ昇順に並べる）。</param>
    /// <param name="representedRanks">各位置が表す数字（基本強さ昇順）。ジョーカーのみの出しは <see cref="Rank.Joker"/>。</param>
    /// <param name="suitPattern">縛り判定用のスートパターン。<see cref="Suit.Joker"/> はワイルド。</param>
    /// <param name="minBaseStrength">表す数字のうち最小の基本強さ。</param>
    /// <param name="maxBaseStrength">表す数字のうち最大の基本強さ。</param>
    public CardPlay(
        HandType type,
        IReadOnlyList<Card> cards,
        IReadOnlyList<Rank> representedRanks,
        IReadOnlyList<Suit> suitPattern,
        int minBaseStrength,
        int maxBaseStrength)
    {
        if (cards is null || cards.Count == 0)
        {
            throw new ArgumentException("カードが空です。", nameof(cards));
        }
        if (representedRanks is null || representedRanks.Count != cards.Count)
        {
            throw new ArgumentException("representedRanks の要素数がカード枚数と一致しません。", nameof(representedRanks));
        }
        if (suitPattern is null || suitPattern.Count != cards.Count)
        {
            throw new ArgumentException("suitPattern の要素数がカード枚数と一致しません。", nameof(suitPattern));
        }
        if (type == HandType.Invalid)
        {
            throw new ArgumentException("Invalid 種別の出し札は生成できません。", nameof(type));
        }

        Type = type;
        Cards = cards.ToArray();
        RepresentedRanks = representedRanks.ToArray();
        SuitPattern = suitPattern.ToArray();
        MinBaseStrength = minBaseStrength;
        MaxBaseStrength = maxBaseStrength;
        NaturalCards = Cards.Where(c => !c.IsJoker).ToArray();
        JokerCount = Cards.Count - NaturalCards.Count;
        JokerSubstitutedRanks = ComputeJokerSubstitutes();
    }

    /// <summary>組み合わせ種別。</summary>
    public HandType Type { get; }

    /// <summary>出したカード（階段は位置順）。</summary>
    public IReadOnlyList<Card> Cards { get; }

    /// <summary>ジョーカー以外のカード。</summary>
    public IReadOnlyList<Card> NaturalCards { get; }

    /// <summary>枚数。</summary>
    public int Count => Cards.Count;

    /// <summary>含まれるジョーカーの枚数。</summary>
    public int JokerCount { get; }

    /// <summary>ジョーカーのみで構成されているか（単体ジョーカー、ジョーカー2枚出し）。</summary>
    public bool IsJokerOnly => JokerCount == Count;

    /// <summary>階段またはエンペラー（連番系）か。</summary>
    public bool IsSequenceLike => Type is HandType.Sequence or HandType.Emperor;

    /// <summary>各位置が表す数字（基本強さ昇順）。</summary>
    public IReadOnlyList<Rank> RepresentedRanks { get; }

    /// <summary>ジョーカーが代用している数字の一覧。</summary>
    public IReadOnlyList<Rank> JokerSubstitutedRanks { get; }

    /// <summary>縛り判定用スートパターン（<see cref="Suit.Joker"/> はワイルド）。</summary>
    public IReadOnlyList<Suit> SuitPattern { get; }

    /// <summary>代表数字（単騎・複数枚出しの数字、連番系は最弱位置の数字）。</summary>
    public Rank Rank => RepresentedRanks[0];

    /// <summary>表す数字の最小基本強さ。</summary>
    public int MinBaseStrength { get; }

    /// <summary>表す数字の最大基本強さ。</summary>
    public int MaxBaseStrength { get; }

    /// <summary>
    /// 反転状態を考慮した強さの範囲を返す。単騎・複数枚出しは Low == High。
    /// 階段の比較は「新しい階段の Low ＞ 場の階段の High」で行う。
    /// </summary>
    public (int Low, int High) GetStrengthRange(bool reversed)
    {
        if (IsJokerOnly)
        {
            return (Card.JokerStrength, Card.JokerStrength);
        }
        int a = Card.ApplyReversal(MinBaseStrength, reversed);
        int b = Card.ApplyReversal(MaxBaseStrength, reversed);
        return (Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>
    /// 指定数字を表すカードの枚数を数える（特殊効果の発動枚数算出用）。
    /// </summary>
    /// <param name="rank">数字。</param>
    /// <param name="includeJokerSubstitutes">ジョーカーが代用している分も数えるか。</param>
    public int CountRank(Rank rank, bool includeJokerSubstitutes)
    {
        int n = 0;
        foreach (var c in NaturalCards)
        {
            if (c.Rank == rank)
            {
                n++;
            }
        }
        if (includeJokerSubstitutes)
        {
            foreach (var r in JokerSubstitutedRanks)
            {
                if (r == rank)
                {
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>指定の自然札を含むか。</summary>
    public bool Contains(Suit suit, Rank rank) => NaturalCards.Any(c => c.Suit == suit && c.Rank == rank);

    /// <summary>自然札のスート一覧（ジョーカーを除く、昇順）。</summary>
    public IReadOnlyList<Suit> NaturalSuits => NaturalCards.Select(c => c.Suit).OrderBy(s => s).ToArray();

    /// <summary>同じカード集合か（解釈違いの判定用）。</summary>
    public bool HasSameCards(CardPlay other)
    {
        if (other.Count != Count)
        {
            return false;
        }
        var ids = new HashSet<int>(Cards.Select(c => c.Id));
        return other.Cards.All(c => ids.Contains(c.Id));
    }

    public override string ToString()
    {
        string cards = string.Join(" ", Cards.Select(c => c.ToString()));
        return IsSequenceLike
            ? $"{Type}[{cards}] ({Card.RankCode(RepresentedRanks[0])}-{Card.RankCode(RepresentedRanks[Count - 1])})"
            : $"{Type}[{cards}]";
    }

    private IReadOnlyList<Rank> ComputeJokerSubstitutes()
    {
        if (JokerCount == 0 || IsJokerOnly)
        {
            return Array.Empty<Rank>();
        }
        if (!IsSequenceLike)
        {
            return Enumerable.Repeat(Rank, JokerCount).ToArray();
        }
        // 連番系：表す数字から自然札の数字を除いた残りがジョーカーの代用分
        var remaining = RepresentedRanks.ToList();
        foreach (var c in NaturalCards)
        {
            remaining.Remove(c.Rank);
        }
        return remaining.ToArray();
    }
}
