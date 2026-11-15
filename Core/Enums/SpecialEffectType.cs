namespace DTTS.Core.Enums;

/// <summary>
/// 出し札によって発動する特殊効果の種類。
/// 発動条件・トリガー数字はすべて <c>RuleSettings</c> 側で定義する。
/// </summary>
public enum SpecialEffectType : byte
{
    // ---- 1. 革命系 ----
    /// <summary>革命（同数字 N 枚以上）。強さの序列が反転する。</summary>
    Revolution,
    /// <summary>階段革命（N 枚以上の階段）。</summary>
    SequenceRevolution,
    /// <summary>クーデター（既定：9を3枚）による革命。</summary>
    Coup,
    /// <summary>オーメン（既定：6を3枚）による革命。以後そのラウンド中は革命が起きなくなる。</summary>
    Omen,
    /// <summary>ナナサン革命（既定：7を3枚）による革命。</summary>
    Nanasan,

    // ---- 2. 階段系 ----
    /// <summary>エンペラー（4スート4枚連番）。「エンペラーで場を流す」設定のときだけ場を流す。</summary>
    Emperor,

    // ---- 3. 縛り系 ----
    /// <summary>スート縛りの成立。</summary>
    SuitLock,
    /// <summary>数しば（連番縛り）の成立。</summary>
    NumberLock,

    // ---- 4. 流し・切り系 ----
    /// <summary>8切り。場を流す。</summary>
    EightCut,
    /// <summary>4止め。直前の8切りを無効化する。</summary>
    FourStop,
    /// <summary>砂嵐（既定：3を3枚）。同枚数の何にでも勝ち、場を流す。</summary>
    Sandstorm,
    /// <summary>99車（救急車、既定：9を2枚）。同枚数の何にでも勝ち、場を流す。</summary>
    Ambulance,
    /// <summary>ろくろ首（既定：6を2枚）。場を流す。</summary>
    Rokurokubi,

    // ---- 5. 11バック系 ----
    /// <summary>11バック。場が流れるまで強さが反転する。</summary>
    ElevenBack,
    /// <summary>強化11バック。場が流れても次の11バックまで反転が持続する。</summary>
    StrongElevenBack,

    // ---- 6. スキップ・リバース系 ----
    /// <summary>5スキップ。出した枚数分、後続プレイヤーを飛ばす。</summary>
    FiveSkip,
    /// <summary>13（K）スキップ。</summary>
    ThirteenSkip,
    /// <summary>9リバース。手番の進行方向を反転する。</summary>
    NineReverse,
    /// <summary>12（Q）リバース。</summary>
    TwelveReverse,

    // ---- 7. 特殊効果系 ----
    /// <summary>7渡し。出した枚数分、次のプレイヤーへ手札を渡す。</summary>
    SevenPass,
    /// <summary>10捨て。出した枚数分、手札を捨てられる。</summary>
    TenDiscard,
    /// <summary>12ボンバー。出した枚数分の数字を宣言し、全員がその数字を捨てる。</summary>
    TwelveBomber,
    /// <summary>ダウンナンバー。同スートで1つ弱い数字を出して場を継続した。</summary>
    DownNumber,

    // ---- 8. ジョーカー系 ----
    /// <summary>スペ3返し。単体ジョーカーに♠3で勝ち、場を流す。</summary>
    SpadeThreeReturn,
}

/// <summary>
/// <see cref="SpecialEffectType"/> の補助メソッド。
/// </summary>
public static class SpecialEffectTypeExtensions
{
    /// <summary>革命（強さ反転のトグル）を引き起こす効果かどうか。</summary>
    public static bool IsRevolutionTrigger(this SpecialEffectType type) => type is
        SpecialEffectType.Revolution or
        SpecialEffectType.SequenceRevolution or
        SpecialEffectType.Coup or
        SpecialEffectType.Omen or
        SpecialEffectType.Nanasan;

    /// <summary>その効果が単独で場を流すかどうか。</summary>
    public static bool FlushesField(this SpecialEffectType type) => type is
        SpecialEffectType.EightCut or
        SpecialEffectType.Sandstorm or
        SpecialEffectType.Ambulance or
        SpecialEffectType.Rokurokubi or
        SpecialEffectType.Emperor or
        SpecialEffectType.SpadeThreeReturn;
}
