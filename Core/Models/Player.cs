using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;

namespace DTTS.Core.Models;

/// <summary>
/// プレイヤー（座席）の状態。
/// 手札・階級・得点の変更は GameEngine のみが行う（setter は internal）。
/// </summary>
public sealed class Player
{
    private readonly List<Card> _hand = new();

    /// <summary>プレイヤーを生成する。</summary>
    /// <param name="id">プレイヤーID（ゲーム中不変。席替えしても変わらない）。</param>
    /// <param name="name">表示名。</param>
    /// <param name="kind">操作主体。</param>
    public Player(int id, string name, PlayerKind kind)
    {
        Id = id;
        Name = name ?? string.Empty;
        Kind = kind;
    }

    /// <summary>プレイヤーID。</summary>
    public int Id { get; }

    /// <summary>表示名。</summary>
    public string Name { get; internal set; }

    /// <summary>操作主体（通信切断時は Npc へ切り替わる）。</summary>
    public PlayerKind Kind { get; internal set; }

    /// <summary>手札（強さ順に整列済み）。</summary>
    public IReadOnlyList<Card> Hand => _hand;

    /// <summary>手札枚数。</summary>
    public int HandCount => _hand.Count;

    /// <summary>現在の階級（直前のラウンドの結果）。初回ラウンド中は <see cref="PlayerRank.None"/>。</summary>
    public PlayerRank Rank { get; internal set; }

    /// <summary>このラウンドを抜けたか（あがり・反則・都落ち）。</summary>
    public bool IsFinished { get; internal set; }

    /// <summary>直前のラウンドの最終順位（1始まり）。</summary>
    public int? FinishPosition { get; internal set; }

    /// <summary>累計得点。</summary>
    public int TotalScore { get; internal set; }

    /// <summary>このラウンドでパスした回数。</summary>
    public int PassCountThisRound { get; internal set; }

    /// <summary>指定カードをすべて手札に持っているか（重複指定は不可）。</summary>
    public bool HasAll(IEnumerable<Card> cards)
    {
        var list = cards.ToList();
        if (list.Select(c => c.Id).Distinct().Count() != list.Count)
        {
            return false;
        }
        return list.All(_hand.Contains);
    }

    internal void ResetForRound()
    {
        _hand.Clear();
        IsFinished = false;
        PassCountThisRound = 0;
    }

    internal void SetHand(IEnumerable<Card> cards)
    {
        _hand.Clear();
        _hand.AddRange(cards);
        _hand.Sort();
    }

    internal void AddCards(IEnumerable<Card> cards)
    {
        _hand.AddRange(cards);
        _hand.Sort();
    }

    internal void RemoveCards(IEnumerable<Card> cards)
    {
        foreach (var card in cards.ToList())
        {
            if (!_hand.Remove(card))
            {
                throw new InvalidOperationException($"手札に {card} がありません。");
            }
        }
    }

    internal IReadOnlyList<Card> TakeAll()
    {
        var all = _hand.ToArray();
        _hand.Clear();
        return all;
    }

    public override string ToString() => $"{Name}(#{Id}) {Rank} 手札{HandCount}枚 {TotalScore}点";
}
