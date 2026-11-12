namespace DTTS.Core.Enums;

/// <summary>
/// カードの数字。値はトランプ上の数字そのもの（A=1 … K=13）。
/// 強さの順序（3が最弱、2が最強）は <c>Card.GetBaseStrength</c> で別途算出する。
/// </summary>
public enum Rank : byte
{
    /// <summary>ジョーカー</summary>
    Joker = 0,
    Ace = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Jack = 11,
    Queen = 12,
    King = 13,
}
