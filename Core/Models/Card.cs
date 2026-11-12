using System;
using System.Collections.Generic;
using System.Globalization;
using DTTS.Core.Enums;

namespace DTTS.Core.Models;

/// <summary>
/// トランプ1枚を表す不変オブジェクト。
/// <para>
/// 強さは「基本強さ」（3=1, 4=2 … K=11, A=12, 2=13, Joker=14）を基準とし、
/// 反転状態（革命 XOR 11バック）では自然札の序列のみを反転する（3=13 … 2=1）。
/// ジョーカーは反転の有無に関わらず常に最強（14）。
/// </para>
/// <para>
/// インスタンスは Id（0〜53）ごとに1つだけ生成されキャッシュされる。
/// 等価性は Id で判定するため、ジョーカー2枚も個別に識別できる。
/// </para>
/// </summary>
public sealed class Card : IEquatable<Card>, IComparable<Card>
{
    /// <summary>自然札の最弱強さ。</summary>
    public const int MinStrength = 1;
    /// <summary>自然札の最強強さ。</summary>
    public const int MaxNaturalStrength = 13;
    /// <summary>ジョーカーの強さ（常に最強）。</summary>
    public const int JokerStrength = 14;
    /// <summary>1デッキに含められるジョーカーの最大枚数。</summary>
    public const int MaxJokerCount = 2;
    /// <summary>ジョーカーを除く自然札の枚数。</summary>
    public const int NaturalCardCount = 52;
    /// <summary>1スートあたりの枚数。</summary>
    public const int RanksPerSuit = 13;

    private static readonly Card[] Naturals = BuildNaturals();
    private static readonly Card[] JokerCards =
    {
        new Card(NaturalCardCount, Suit.Joker, Rank.Joker, 0),
        new Card(NaturalCardCount + 1, Suit.Joker, Rank.Joker, 1),
    };

    private Card(int id, Suit suit, Rank rank, int jokerIndex)
    {
        Id = id;
        Suit = suit;
        Rank = rank;
        JokerIndex = jokerIndex;
    }

    /// <summary>カード固有ID。自然札は Suit×13+(Rank−1)、ジョーカーは 52+JokerIndex。</summary>
    public int Id { get; }

    /// <summary>スート。ジョーカーは <see cref="Suit.Joker"/>。</summary>
    public Suit Suit { get; }

    /// <summary>数字。ジョーカーは <see cref="Rank.Joker"/>。</summary>
    public Rank Rank { get; }

    /// <summary>ジョーカーの個別番号（0 または 1）。自然札は −1。</summary>
    public int JokerIndex { get; }

    /// <summary>ジョーカーかどうか。</summary>
    public bool IsJoker => Suit == Suit.Joker;

    /// <summary>反転なし状態での強さ（3=1 … 2=13, Joker=14）。</summary>
    public int BaseStrength => IsJoker ? JokerStrength : GetBaseStrength(Rank);

    /// <summary>
    /// 現在の場の状態を考慮した強さを返す。
    /// </summary>
    /// <param name="reversed">強さが反転しているか（革命 XOR 11バック）。</param>
    public int GetStrength(bool reversed) => ApplyReversal(BaseStrength, reversed);

    // ------------------------------------------------------------------
    // ファクトリ
    // ------------------------------------------------------------------

    /// <summary>自然札を取得する。</summary>
    /// <exception cref="ArgumentOutOfRangeException">スートまたは数字が不正な場合。</exception>
    public static Card Of(Suit suit, Rank rank)
    {
        if (suit == Suit.Joker || rank == Rank.Joker)
        {
            throw new ArgumentOutOfRangeException(nameof(suit), "ジョーカーは Card.Joker(index) で取得してください。");
        }
        if (!Enum.IsDefined(suit) || (int)rank < 1 || (int)rank > RanksPerSuit)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), $"不正なカード指定です: {suit} {rank}");
        }
        return Naturals[((int)suit * RanksPerSuit) + ((int)rank - 1)];
    }

    /// <summary>ジョーカーを取得する。</summary>
    /// <param name="index">ジョーカー個別番号（0 または 1）。</param>
    public static Card Joker(int index = 0)
    {
        if (index < 0 || index >= MaxJokerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"ジョーカー番号は 0〜{MaxJokerCount - 1} です。");
        }
        return JokerCards[index];
    }

    /// <summary>IDからカードを取得する（永続化・通信の復元用）。</summary>
    public static Card FromId(int id)
    {
        if (id >= 0 && id < NaturalCardCount)
        {
            return Naturals[id];
        }
        if (id >= NaturalCardCount && id < NaturalCardCount + MaxJokerCount)
        {
            return JokerCards[id - NaturalCardCount];
        }
        throw new ArgumentOutOfRangeException(nameof(id), $"不正なカードIDです: {id}");
    }

    /// <summary>
    /// 新しいデッキ（未シャッフル）を生成する。シャッフルは呼び出し側（GameEngine）で
    /// 乱数源を注入して行うことで、テスト時に再現性を確保する。
    /// </summary>
    /// <param name="jokerCount">ジョーカー枚数（0〜2）。</param>
    public static IReadOnlyList<Card> CreateDeck(int jokerCount)
    {
        if (jokerCount < 0 || jokerCount > MaxJokerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(jokerCount));
        }
        var deck = new List<Card>(NaturalCardCount + jokerCount);
        deck.AddRange(Naturals);
        for (int i = 0; i < jokerCount; i++)
        {
            deck.Add(JokerCards[i]);
        }
        return deck;
    }

    // ------------------------------------------------------------------
    // 強さ計算ユーティリティ
    // ------------------------------------------------------------------

    /// <summary>数字から基本強さ（3=1 … K=11, A=12, 2=13, Joker=14）を求める。</summary>
    public static int GetBaseStrength(Rank rank)
    {
        if (rank == Rank.Joker)
        {
            return JokerStrength;
        }
        int r = (int)rank;
        if (r < 1 || r > RanksPerSuit)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }
        // 3→1, 4→2, …, K→11, A→12, 2→13
        return ((r + 10) % RanksPerSuit) + 1;
    }

    /// <summary>基本強さから数字を逆算する。</summary>
    public static Rank GetRankFromBaseStrength(int baseStrength)
    {
        if (baseStrength == JokerStrength)
        {
            return Rank.Joker;
        }
        if (baseStrength < MinStrength || baseStrength > MaxNaturalStrength)
        {
            throw new ArgumentOutOfRangeException(nameof(baseStrength));
        }
        // 1→3, 11→K(13), 12→A(1), 13→2
        return (Rank)(((baseStrength + 1) % RanksPerSuit) + 1);
    }

    /// <summary>基本強さに反転状態を適用する。ジョーカー（14）は反転しない。</summary>
    public static int ApplyReversal(int baseStrength, bool reversed)
    {
        if (baseStrength == JokerStrength || !reversed)
        {
            return baseStrength;
        }
        return (MaxNaturalStrength + 1) - baseStrength;
    }

    /// <summary>数字の強さを反転状態込みで求める。</summary>
    public static int GetStrength(Rank rank, bool reversed) => ApplyReversal(GetBaseStrength(rank), reversed);

    // ------------------------------------------------------------------
    // 文字列表現
    // ------------------------------------------------------------------

    /// <summary>
    /// 永続化・テスト用の短いコード（例: "S3", "H10", "DQ", "CA", "JK0"）。
    /// </summary>
    public string Code => IsJoker
        ? "JK" + JokerIndex.ToString(CultureInfo.InvariantCulture)
        : SuitCode(Suit) + RankCode(Rank);

    /// <summary>表示用文字列（例: "♠3", "♥Q", "JOKER1"）。</summary>
    public override string ToString() => IsJoker
        ? "JOKER" + (JokerIndex + 1).ToString(CultureInfo.InvariantCulture)
        : SuitSymbol(Suit) + RankCode(Rank);

    /// <summary>コード文字列（"S3", "H10", "JK", "JK1" など、大小文字不問）からカードを取得する。</summary>
    public static Card Parse(string code)
    {
        if (TryParse(code, out var card))
        {
            return card;
        }
        throw new FormatException($"カードコードを解釈できません: '{code}'");
    }

    /// <summary>コード文字列の解析を試みる。</summary>
    public static bool TryParse(string? code, out Card card)
    {
        card = null!;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }
        string s = code.Trim().ToUpperInvariant();

        if (s.StartsWith("JK", StringComparison.Ordinal))
        {
            string rest = s.Substring(2);
            int index = 0;
            if (rest.Length > 0 && !int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index))
            {
                return false;
            }
            if (index < 0 || index >= MaxJokerCount)
            {
                return false;
            }
            card = JokerCards[index];
            return true;
        }

        Suit suit;
        switch (s[0])
        {
            case 'S': case '♠': suit = Suit.Spade; break;
            case 'H': case '♥': suit = Suit.Heart; break;
            case 'D': case '♦': suit = Suit.Diamond; break;
            case 'C': case '♣': suit = Suit.Club; break;
            default: return false;
        }

        string r = s.Substring(1);
        Rank rank;
        switch (r)
        {
            case "A": case "1": rank = Rank.Ace; break;
            case "J": rank = Rank.Jack; break;
            case "Q": rank = Rank.Queen; break;
            case "K": rank = Rank.King; break;
            default:
                if (!int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 2 || n > 13)
                {
                    return false;
                }
                rank = (Rank)n;
                break;
        }
        card = Of(suit, rank);
        return true;
    }

    /// <summary>スペース区切りのコード列（例: "S3 H3 JK"）を一括解析する。テスト記述の簡略化用。</summary>
    public static IReadOnlyList<Card> ParseMany(string codes)
    {
        var list = new List<Card>();
        foreach (var token in codes.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            list.Add(Parse(token));
        }
        return list;
    }

    /// <summary>スートの表示記号。</summary>
    public static string SuitSymbol(Suit suit) => suit switch
    {
        Suit.Spade => "♠",
        Suit.Heart => "♥",
        Suit.Diamond => "♦",
        Suit.Club => "♣",
        _ => "★",
    };

    private static string SuitCode(Suit suit) => suit switch
    {
        Suit.Spade => "S",
        Suit.Heart => "H",
        Suit.Diamond => "D",
        Suit.Club => "C",
        _ => "JK",
    };

    /// <summary>数字の表示文字（A, 2〜10, J, Q, K）。</summary>
    public static string RankCode(Rank rank) => rank switch
    {
        Rank.Ace => "A",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Joker => "JK",
        _ => ((int)rank).ToString(CultureInfo.InvariantCulture),
    };

    // ------------------------------------------------------------------
    // 等価性・比較
    // ------------------------------------------------------------------

    public bool Equals(Card? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => obj is Card c && c.Id == Id;

    public override int GetHashCode() => Id;

    /// <summary>手札の並び順（基本強さ → スート）で比較する。</summary>
    public int CompareTo(Card? other)
    {
        if (other is null)
        {
            return 1;
        }
        int c = BaseStrength.CompareTo(other.BaseStrength);
        return c != 0 ? c : Id.CompareTo(other.Id);
    }

    public static bool operator ==(Card? left, Card? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Card? left, Card? right) => !(left == right);

    private static Card[] BuildNaturals()
    {
        var arr = new Card[NaturalCardCount];
        for (int s = 0; s < 4; s++)
        {
            for (int r = 1; r <= RanksPerSuit; r++)
            {
                int id = (s * RanksPerSuit) + (r - 1);
                arr[id] = new Card(id, (Suit)s, (Rank)r, -1);
            }
        }
        return arr;
    }
}
