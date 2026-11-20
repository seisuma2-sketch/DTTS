namespace DTTS.Core.Enums;

/// <summary>出し札が不正と判定された理由。</summary>
public enum PlayRejectReason : byte
{
    /// <summary>不正なし（合法手）。</summary>
    None = 0,
    /// <summary>カードが選択されていない。</summary>
    NoCards,
    /// <summary>役として成立しない組み合わせ（重複カードを含む）。</summary>
    InvalidCombination,
    /// <summary>場と組み合わせ種別が異なる（単騎に対してペア等）。</summary>
    TypeMismatch,
    /// <summary>場と枚数が異なる。</summary>
    CountMismatch,
    /// <summary>場のカードより強くない。</summary>
    NotStronger,
    /// <summary>スート縛りに合致しない。</summary>
    SuitLockViolation,
    /// <summary>数しばに合致しない（ちょうど1つ上ではない）。</summary>
    NumberLockViolation,
    /// <summary>初手に必須のカード（ダイヤ3等）が含まれていない。</summary>
    MissingRequiredCard,
    /// <summary>8切りの割り込み受付中で、4止め以外は出せない。</summary>
    FourStopOnly,
    /// <summary>禁止あがり（厳格判定：その手ではあがれない設定）。</summary>
    ForbiddenFinish,
    /// <summary>ろくろ首・99車の割り込み受付中で、砂嵐以外は出せない。</summary>
    SandstormStopOnly,
    /// <summary>4止めが成立した後で、場が流れるまで砂嵐以外は出せない。</summary>
    FourStopLocked,
    /// <summary>クーデターが成立した後で、場が流れるまで6・7・砂嵐以外は出せない。</summary>
    CoupLocked,
}
