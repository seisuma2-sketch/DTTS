using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

namespace DTTS.Core.Ai;

/// <summary>
/// NPCの思考戦略。<see cref="NpcContext"/>（そのプレイヤーから見える情報のみ）を受け取り、判断を返す純粋なロジック。
/// エンジンへの依存はなく、任意の盤面を与えて単体テストできる。
/// </summary>
public interface INpcStrategy
{
    /// <summary>この戦略の設定。</summary>
    NpcProfile Profile { get; }

    /// <summary>出す手を決める（手番・4止めの機会）。</summary>
    NpcPlayDecision DecidePlay(NpcContext context);

    /// <summary>カードを選ぶ（交換・7渡し・10捨て）。</summary>
    IReadOnlyList<Card> SelectCards(NpcContext context);

    /// <summary>12ボンバーで宣言する数字を決める。</summary>
    IReadOnlyList<Rank> DeclareRanks(NpcContext context);

    /// <summary>革命選択で革命を起こすか決める。</summary>
    bool ChooseRevolution(NpcContext context);
}

/// <summary>出し札の判断結果。</summary>
/// <param name="Cards">出すカード。null ならパス。</param>
public sealed record NpcPlayDecision(IReadOnlyList<Card>? Cards)
{
    /// <summary>パス。</summary>
    public static NpcPlayDecision Pass { get; } = new((IReadOnlyList<Card>?)null);

    /// <summary>パスか。</summary>
    public bool IsPass => Cards is null;
}

/// <summary>
/// NPCが判断に使う情報。他プレイヤーの手札は含まず、公開情報（枚数・場に出たカード）のみを持つ。
/// </summary>
public sealed class NpcContext
{
    /// <summary>判断材料を生成する（テストでは任意の盤面を直接指定できる）。</summary>
    public NpcContext(
        int playerId,
        IReadOnlyList<Card> hand,
        FieldState field,
        RuleSettings rules,
        IJudgeLogic judge,
        PendingDecision decision,
        IReadOnlyList<PlayJudgement> legalPlays,
        IReadOnlyDictionary<int, int> opponentHandCounts,
        IReadOnlyCollection<Card> seenCards,
        PlayerRank myRank = PlayerRank.None)
    {
        PlayerId = playerId;
        Hand = hand ?? throw new ArgumentNullException(nameof(hand));
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        Judge = judge ?? throw new ArgumentNullException(nameof(judge));
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        LegalPlays = legalPlays ?? Array.Empty<PlayJudgement>();
        OpponentHandCounts = opponentHandCounts ?? new Dictionary<int, int>();
        SeenCards = seenCards ?? Array.Empty<Card>();
        MyRank = myRank;
    }

    /// <summary>自分のプレイヤーID。</summary>
    public int PlayerId { get; }

    /// <summary>自分の手札。</summary>
    public IReadOnlyList<Card> Hand { get; }

    /// <summary>場の状態。</summary>
    public FieldState Field { get; }

    /// <summary>ルール設定。</summary>
    public RuleSettings Rules { get; }

    /// <summary>判定ロジック。</summary>
    public IJudgeLogic Judge { get; }

    /// <summary>求められている判断。</summary>
    public PendingDecision Decision { get; }

    /// <summary>現在出せる合法手（手番・4止め以外では空）。</summary>
    public IReadOnlyList<PlayJudgement> LegalPlays { get; }

    /// <summary>まだ残っている対戦相手の手札枚数（キーはプレイヤーID）。</summary>
    public IReadOnlyDictionary<int, int> OpponentHandCounts { get; }

    /// <summary>このラウンドで公開されたカード（場に出た・捨てられた）。</summary>
    public IReadOnlyCollection<Card> SeenCards { get; }

    /// <summary>自分の現在の階級。</summary>
    public PlayerRank MyRank { get; }

    /// <summary>対戦相手の最少手札枚数（全員あがっていれば int.MaxValue）。</summary>
    public int MinOpponentHandCount => OpponentHandCounts.Values.Where(n => n > 0).DefaultIfEmpty(int.MaxValue).Min();

    /// <summary>自分から見て所在不明のカード（＝相手の誰かが持っている可能性のあるカード）。</summary>
    public IReadOnlyList<Card> GetUnseenCards()
    {
        var known = new HashSet<int>(Hand.Select(c => c.Id));
        known.UnionWith(SeenCards.Select(c => c.Id));
        return Card.CreateDeck(Rules.Joker.JokerCount).Where(c => !known.Contains(c.Id)).ToList();
    }

    /// <summary>エンジンの現在の状態から判断材料を生成する。</summary>
    public static NpcContext FromEngine(GameEngine engine, int playerId, IReadOnlyCollection<Card> seenCards)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var decision = engine.PendingDecision
            ?? throw new InvalidOperationException("判断待ちではありません。");
        var me = engine.GetPlayer(playerId);
        var opponents = engine.Players
            .Where(p => p.Id != playerId && !p.IsFinished)
            .ToDictionary(p => p.Id, p => p.HandCount);
        return new NpcContext(
            playerId,
            me.Hand.ToArray(),
            engine.Field,
            engine.Rules,
            engine.Judge,
            decision,
            engine.GetLegalPlays(playerId),
            opponents,
            seenCards.ToArray(),
            me.Rank);
    }
}
