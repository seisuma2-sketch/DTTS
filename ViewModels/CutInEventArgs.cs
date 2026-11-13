using System;

namespace DTTS.ViewModels;

/// <summary>
/// カットイン演出のパラメータ。
/// </summary>
public sealed class CutInEventArgs : EventArgs
{
    public string Title { get; }
    public string SubTitle { get; }
    public string Detail { get; }
    public string AccentColorHex { get; }

    /// <summary>タイトルの上に表示する線アイコン（Segoe Fluent Icons の文字。<see cref="IconGlyphs"/> を参照）。</summary>
    public string Glyph { get; }

    public CutInEventArgs(string title, string subTitle, string detail, string accentColorHex = "#FFD700", string glyph = IconGlyphs.LightningBolt)
    {
        Title = title;
        SubTitle = subTitle;
        Detail = detail;
        AccentColorHex = accentColorHex;
        Glyph = glyph;
    }
}

/// <summary>
/// 画面で使う線アイコン（Segoe Fluent Icons / Segoe MDL2 Assets の文字コード）。
/// カラー絵文字は環境によって見た目が変わり、配色にも合わないため使わない。
/// </summary>
public static class IconGlyphs
{
    public const string Switch = "";        // 革命（強弱の入れ替え）
    public const string RepeatAll = "";     // 11バック（一時的な反転）
    public const string LightningBolt = ""; // 8切り・ろくろ首
    public const string Undo = "";          // スペ3返し
    public const string Star = "";          // エンペラー
    public const string Cloud = "";         // 砂嵐
    public const string Car = "";           // 99車
    public const string Warning = "";       // 12ボンバー
    public const string Send = "";          // 7渡し
    public const string Delete = "";        // 10捨て
    public const string Lock = "";          // 縛り
    public const string Stop = "";          // 4止め
    public const string ChevronDown = "";   // 都落ち
    public const string ChevronUp = "";     // 下剋上
}
