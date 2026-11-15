using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Models;

namespace DTTS.Services.Presentation;

/// <summary>
/// カード交換の結果（自分が渡したカード・受け取ったカード）を集める。
/// ラウンドの始めに <see cref="Clear"/> し、交換によるカードの移動を <see cref="Record"/> に渡す。
/// 自分に関係のない移動（他の人どうしの交換）は無視する。
/// </summary>
public sealed class ExchangeSummary
{
    private readonly List<(int Partner, List<Card> Cards)> _given = new();
    private readonly List<(int Partner, List<Card> Cards)> _received = new();

    /// <summary>自分が渡したカード（相手ごと）。</summary>
    public IReadOnlyList<(int Partner, List<Card> Cards)> Given => _given;

    /// <summary>自分が受け取ったカード（相手ごと）。</summary>
    public IReadOnlyList<(int Partner, List<Card> Cards)> Received => _received;

    /// <summary>何か記録されているか。</summary>
    public bool HasAny => _given.Count > 0 || _received.Count > 0;

    /// <summary>新しいラウンド：記録を消す。</summary>
    public void Clear()
    {
        _given.Clear();
        _received.Clear();
    }

    /// <summary>交換によるカードの移動を記録する。</summary>
    /// <param name="viewerId">画面を見ている人（自分）。</param>
    /// <param name="fromId">渡した人。</param>
    /// <param name="toId">受け取った人。</param>
    /// <param name="cards">移動したカード（自分に関係する移動なら中身が分かる）。</param>
    /// <returns>自分に関係する移動だったら true。</returns>
    public bool Record(int viewerId, int fromId, int toId, IEnumerable<Card>? cards)
    {
        var list = cards?.ToList() ?? new List<Card>();
        if (list.Count == 0)
        {
            return false;
        }
        if (fromId == viewerId)
        {
            Add(_given, toId, list);
            return true;
        }
        if (toId == viewerId)
        {
            Add(_received, fromId, list);
            return true;
        }
        return false;
    }

    private static void Add(List<(int Partner, List<Card> Cards)> target, int partner, List<Card> cards)
    {
        int i = target.FindIndex(x => x.Partner == partner);
        if (i >= 0)
        {
            target[i].Cards.AddRange(cards);
        }
        else
        {
            target.Add((partner, new List<Card>(cards)));
        }
    }

    /// <summary>
    /// 1行の説明（例：「大富豪 ミオ へ ♠3・♦4 を渡し、♠2・♥A を受け取りました」）。
    /// </summary>
    /// <param name="partnerLabel">相手の表示名（階級＋名前など）。</param>
    public string Describe(Func<int, string> partnerLabel)
    {
        ArgumentNullException.ThrowIfNull(partnerLabel);
        static string CardsText(IEnumerable<Card> cards) => string.Join("・", cards.Select(c => c.ToString()));
        var parts = new List<string>();
        foreach (var (partner, cards) in _given)
        {
            parts.Add($"{partnerLabel(partner)} へ {CardsText(cards)} を渡し");
        }
        foreach (var (partner, cards) in _received)
        {
            parts.Add($"{partnerLabel(partner)} から {CardsText(cards)} を受け取り");
        }
        return parts.Count == 0 ? "" : string.Join("、", parts) + "ました";
    }
}
