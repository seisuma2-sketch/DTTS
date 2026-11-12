namespace DTTS.Core.Enums;

/// <summary>
/// カードのスート（マーク）。
/// Joker はスートを持たないため専用値 <see cref="Joker"/> を割り当てる。
/// 縛り判定のスートパターンでは <see cref="Joker"/> を「ワイルド（任意スート）」の意味でも用いる。
/// </summary>
public enum Suit : byte
{
    /// <summary>スペード ♠</summary>
    Spade = 0,
    /// <summary>ハート ♥</summary>
    Heart = 1,
    /// <summary>ダイヤ ♦</summary>
    Diamond = 2,
    /// <summary>クラブ ♣</summary>
    Club = 3,
    /// <summary>ジョーカー（スートなし／ワイルド）</summary>
    Joker = 4,
}
