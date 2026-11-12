using System;

namespace DTTS.Core.Enums;

/// <summary>
/// 禁止あがりに該当した理由（複数該当し得るためフラグ列挙）。
/// </summary>
[Flags]
public enum ForbiddenFinishReason : ushort
{
    None = 0,
    /// <summary>最強数字（通常時は2、反転時は3 ※設定による）を含む。</summary>
    StrongestNumber = 1 << 0,
    /// <summary>8切りが発動する出し札。</summary>
    EightCut = 1 << 1,
    /// <summary>ジョーカーを含む。</summary>
    Joker = 1 << 2,
    /// <summary>スペ3返しとして出した。</summary>
    SpadeThree = 1 << 3,
    /// <summary>12ボンバーが発動する出し札。</summary>
    TwelveBomber = 1 << 4,
    /// <summary>11バックが発動する出し札。</summary>
    ElevenBack = 1 << 5,
    /// <summary>その他、場を流す特殊効果（砂嵐・99車・ろくろ首・エンペラー）が発動する出し札。</summary>
    FlushEffect = 1 << 6,
    /// <summary>革命を発生させる出し札。</summary>
    Revolution = 1 << 7,
}

/// <summary>禁止あがりをした場合のペナルティ方式。</summary>
public enum ForbiddenFinishPenalty : byte
{
    /// <summary>あがりは認めるが反則あがりとして最下位（大貧民）扱い。</summary>
    LastPlace = 0,
    /// <summary>その手ではあがれない（出し札そのものを不正として拒否する）。</summary>
    RejectPlay = 1,
}
