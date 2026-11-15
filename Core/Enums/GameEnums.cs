namespace DTTS.Core.Enums;

/// <summary>ゲーム全体の進行フェーズ。</summary>
public enum GamePhase : byte
{
    /// <summary>ゲーム開始前。</summary>
    NotStarted = 0,
    /// <summary>配牌中。</summary>
    Dealing,
    /// <summary>カード交換中。</summary>
    Exchanging,
    /// <summary>対戦中。</summary>
    Playing,
    /// <summary>ラウンド終了（次ラウンド開始待ち）。</summary>
    RoundEnded,
    /// <summary>ゲーム終了。</summary>
    GameEnded,
}

/// <summary>プレイヤーの操作主体。</summary>
public enum PlayerKind : byte
{
    /// <summary>この端末のプレイヤー。</summary>
    Human = 0,
    /// <summary>NPC（思考エンジン）。</summary>
    Npc,
    /// <summary>通信先の端末のプレイヤー。</summary>
    Remote,
}

/// <summary>11バック（強さの一時反転）がいつまで続くか。</summary>
public enum ElevenBackDuration : byte
{
    /// <summary>場が流れるまで（標準）。</summary>
    UntilFlush = 0,
    /// <summary>Jを出した人の次の1人の手番だけ。その人が出す・パスするのいずれかを終えた時点で戻る（場が流れても戻る）。</summary>
    NextTurnOnly,
    /// <summary>場が流れても続き、次にJが出るまで解除されない（強化11バック）。</summary>
    Persistent,
}

/// <summary>4止めで8切りを止めるのに必要な4の枚数。</summary>
public enum FourStopCountRule : byte
{
    /// <summary>8の枚数の2倍（8が1枚なら4が2枚、8が2枚なら4が4枚）。標準。</summary>
    DoubleEight = 0,
    /// <summary>8と同じ枚数（8が1枚なら4が1枚）。</summary>
    SameAsEight,
}

/// <summary>2ラウンド目以降の親の決め方。</summary>
public enum RoundLeaderRule : byte
{
    /// <summary>前ラウンドの大貧民が親。</summary>
    Daihinmin = 0,
    /// <summary>前ラウンドの大富豪が親。</summary>
    Daifugo,
    /// <summary>♦3を持つプレイヤーが親（♦3を含める義務はなし）。</summary>
    DiamondThreeHolder,
    /// <summary>ランダム。</summary>
    Random,
}

/// <summary>エンジンがプレイヤーに求めている判断の種類。</summary>
public enum DecisionKind : byte
{
    /// <summary>手番：カードを出すかパスする。</summary>
    PlayOrPass = 0,
    /// <summary>8切りへの4止め割り込み：4を出すか、パスで見送る。</summary>
    FourStopChance,
    /// <summary>革命選択：革命を起こすかどうか。</summary>
    RevolutionChoice,
    /// <summary>カード選択（交換・7渡し・10捨て）。</summary>
    SelectCards,
    /// <summary>12ボンバーの数字宣言。</summary>
    DeclareRanks,
    /// <summary>ろくろ首・99車への砂嵐の割り込み：砂嵐を出して流しを止めるか、パスで見送る。</summary>
    SandstormStopChance,
}

/// <summary><see cref="DecisionKind"/> の補助メソッド。</summary>
public static class DecisionKindExtensions
{
    /// <summary>カードを出すかパスするかの判断か（手番・4止め・砂嵐の割り込み）。</summary>
    public static bool IsPlayDecision(this DecisionKind kind)
        => kind is DecisionKind.PlayOrPass or DecisionKind.FourStopChance or DecisionKind.SandstormStopChance;

    /// <summary>手番外の割り込みの機会か（4止め・砂嵐）。パスは「見送り」になる。</summary>
    public static bool IsInterruptChance(this DecisionKind kind)
        => kind is DecisionKind.FourStopChance or DecisionKind.SandstormStopChance;
}

/// <summary>カード選択の目的。</summary>
public enum CardSelectionPurpose : byte
{
    /// <summary>カード交換で相手に渡す。</summary>
    ExchangeGive = 0,
    /// <summary>7渡しで次のプレイヤーに渡す。</summary>
    SevenPass,
    /// <summary>10捨てで捨てる。</summary>
    TenDiscard,
}

/// <summary>カードが別プレイヤーへ移動した理由。</summary>
public enum CardTransferReason : byte
{
    /// <summary>カード交換。</summary>
    Exchange = 0,
    /// <summary>7渡し。</summary>
    SevenPass,
    /// <summary>天変地異（手札の全交換）。</summary>
    TenpenChii,
}

/// <summary>カードが捨てられた理由。</summary>
public enum CardDiscardReason : byte
{
    /// <summary>10捨て。</summary>
    TenDiscard = 0,
    /// <summary>12ボンバー。</summary>
    TwelveBomber,
    /// <summary>都落ち（残り手札の没収）。</summary>
    MiyakoOchi,
    /// <summary>親番で出せる手がなく反則負けになった（残り手札の没収）。</summary>
    ForcedFoul,
}

/// <summary>プレイヤーがラウンドを抜けた形態。</summary>
public enum FinishKind : byte
{
    /// <summary>通常のあがり。</summary>
    Normal = 0,
    /// <summary>禁止あがり（反則：下位に配置）。</summary>
    Foul,
    /// <summary>都落ち（最下位に配置）。</summary>
    MiyakoOchi,
    /// <summary>最後に残ったプレイヤー。</summary>
    LastRemaining,
    /// <summary>下剋上でラウンドが打ち切られた。</summary>
    Gekokujo,
}

/// <summary>コマンドの実行結果。</summary>
public enum CommandError : byte
{
    /// <summary>成功。</summary>
    None = 0,
    /// <summary>イベント処理中に呼ばれたため、現在の処理完了後に実行される（失敗時は CommandRejectedEvent で通知）。</summary>
    Deferred,
    /// <summary>現在のフェーズでは実行できない。</summary>
    WrongPhase,
    /// <summary>そのプレイヤーの判断待ちではない。</summary>
    NotYourTurn,
    /// <summary>手札にないカード、または重複したカードが指定された。</summary>
    CardsNotInHand,
    /// <summary>不正な出し札（詳細は PlayRejectReason）。</summary>
    IllegalPlay,
    /// <summary>親（場が空）はパスできない。</summary>
    CannotPassAsLeader,
    /// <summary>パス回数の上限に達している。</summary>
    PassLimitReached,
    /// <summary>選択内容（枚数・数字）が不正。</summary>
    InvalidSelection,
    /// <summary>プレイヤーIDが存在しない。</summary>
    UnknownPlayer,
}
