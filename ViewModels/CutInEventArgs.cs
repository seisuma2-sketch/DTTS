using System;
using System.Collections.Generic;

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

/// <summary>12ボンバーで消えた数字1つ分。</summary>
/// <param name="Rank">宣言された数字（ジョーカーは <see cref="DTTS.Core.Enums.Rank.Joker"/>）。</param>
/// <param name="Label">表示名（3〜10・J・Q・K・A・2・JK）。</param>
/// <param name="DiscardedCount">全員の手札から消えた枚数の合計（誰も持っていなければ 0）。</param>
public sealed record BomberVanishItem(DTTS.Core.Enums.Rank Rank, string Label, int DiscardedCount);

/// <summary>12ボンバーで消えた数字を画面中央で順に見せる演出のパラメータ。</summary>
public sealed class BomberVanishEventArgs : EventArgs
{
    public BomberVanishEventArgs(string declarerName, IReadOnlyList<BomberVanishItem> items)
    {
        DeclarerName = declarerName;
        Items = items;
    }

    /// <summary>宣言したプレイヤーの表示名。</summary>
    public string DeclarerName { get; }

    /// <summary>消えた数字（弱い順：3 … K・A・2・ジョーカー）。</summary>
    public IReadOnlyList<BomberVanishItem> Items { get; }
}

/// <summary>あがりの演出のパラメータ。</summary>
public sealed class FinishCelebrationEventArgs : EventArgs
{
    public FinishCelebrationEventArgs(string playerName, int place, bool isSelf, string rankTitle)
    {
        PlayerName = playerName;
        Place = place;
        IsSelf = isSelf;
        RankTitle = rankTitle;
    }

    /// <summary>あがったプレイヤーの表示名。</summary>
    public string PlayerName { get; }

    /// <summary>順位（1始まり）。</summary>
    public int Place { get; }

    /// <summary>この端末のプレイヤーか（自分のあがりは少し大きく見せる）。</summary>
    public bool IsSelf { get; }

    /// <summary>この順位で得る階級の名前（大富豪・富豪・貧民など）。</summary>
    public string RankTitle { get; }
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
