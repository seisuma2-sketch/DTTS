namespace DTTS.Core.Enums;

/// <summary>
/// プレイヤーの階級。4人戦では 大富豪・富豪・貧民・大貧民 の4段階を使用し、
/// <see cref="Heimin"/>（平民）は5人以上の拡張用に予約している。
/// </summary>
public enum PlayerRank : byte
{
    /// <summary>未確定（初回ラウンド開始前など）</summary>
    None = 0,
    /// <summary>大富豪</summary>
    Daifugo = 1,
    /// <summary>富豪</summary>
    Fugo = 2,
    /// <summary>平民（5人以上用）</summary>
    Heimin = 3,
    /// <summary>貧民</summary>
    Hinmin = 4,
    /// <summary>大貧民</summary>
    Daihinmin = 5,
}
