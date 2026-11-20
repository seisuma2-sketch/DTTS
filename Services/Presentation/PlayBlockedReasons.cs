using DTTS.Core.Enums;

namespace DTTS.Services.Presentation;

/// <summary>選んだカードが出せない理由を、プレイヤー向けの短い文にする（「出す」が押せないときの案内）。</summary>
public static class PlayBlockedReasons
{
    /// <summary>出せない理由の説明（合法なら空文字）。</summary>
    public static string Describe(PlayRejectReason reason) => reason switch
    {
        PlayRejectReason.None => "",
        PlayRejectReason.NoCards => "カードを選んでください",
        PlayRejectReason.InvalidCombination => "その組み合わせは役になりません（同じ数字の組・階段で出します）",
        PlayRejectReason.TypeMismatch => "場と同じ形（1枚・同じ数字の組・階段）で出す必要があります",
        PlayRejectReason.CountMismatch => "場と同じ枚数で出す必要があります",
        PlayRejectReason.NotStronger => "場のカードより強くありません",
        PlayRejectReason.SuitLockViolation => "スート縛り中です：場と同じマークで出してください",
        PlayRejectReason.NumberLockViolation => "数しば中です：場のちょうど1つ上の数字しか出せません",
        PlayRejectReason.MissingRequiredCard => "最初の1手は ♦3 を含めて出してください",
        PlayRejectReason.FourStopOnly => "8切りを止められるのは4止め（または砂嵐）だけです",
        PlayRejectReason.ForbiddenFinish => "そのカードではあがれません（禁止あがり）",
        PlayRejectReason.SandstormStopOnly => "今は砂嵐でしか止められません",
        PlayRejectReason.FourStopLocked => "4止めの後は、場が流れるまで砂嵐しか出せません",
        PlayRejectReason.CoupLocked => "クーデターの後は、場が流れるまで6・7（3枚）か砂嵐しか出せません",
        _ => "そのカードは今は出せません",
    };
}
