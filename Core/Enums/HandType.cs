namespace DTTS.Core.Enums;

/// <summary>
/// 出し札の組み合わせ種別。
/// </summary>
public enum HandType : byte
{
    /// <summary>不正な組み合わせ</summary>
    Invalid = 0,
    /// <summary>1枚出し</summary>
    Single = 1,
    /// <summary>同じ数字の複数枚出し（ペア・スリーカード・4枚以上）</summary>
    Multiple = 2,
    /// <summary>同スートの連番（階段）</summary>
    Sequence = 3,
    /// <summary>エンペラー：4種のスートが1枚ずつの4枚連番（ルール有効時のみ成立）</summary>
    Emperor = 4,
}
