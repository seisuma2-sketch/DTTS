namespace DTTS.Core.Enums;

/// <summary>
/// 出し札が「どの根拠で」合法になったかを表す。UI演出や効果適用の分岐に使用する。
/// </summary>
public enum BeatMethod : byte
{
    /// <summary>場が空の状態での親出し。</summary>
    Lead = 0,
    /// <summary>通常の強さ比較で上回った。</summary>
    Stronger,
    /// <summary>数しば中の「ちょうど1つ上」による出し。</summary>
    NumberLockFollow,
    /// <summary>スペ3返し。</summary>
    SpadeThreeReturn,
    /// <summary>4止め（8切りの割り込み無効化）。</summary>
    FourStop,
    /// <summary>エンペラーによる無条件勝ち。</summary>
    Emperor,
    /// <summary>砂嵐による無条件勝ち。</summary>
    Sandstorm,
    /// <summary>99車による無条件勝ち。</summary>
    Ambulance,
    /// <summary>ダウンナンバー（同スート1つ下）。</summary>
    DownNumber,
    /// <summary>砂嵐による割り込み（ろくろ首・99車の流しを止める。枚数・縛りは問わない）。</summary>
    SandstormStop,
}
