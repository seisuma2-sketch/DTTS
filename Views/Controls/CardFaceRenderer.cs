using System;
using System.Collections.Generic;
using DTTS.Core.Enums;
using DTTS.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DTTS.Views.Controls;

/// <summary>
/// スキン画像がないときのカード表面（ベクター）を組み立てる。
/// 座標はカード全体（70×102）基準の固定値で、小さく表示しても潰れないよう単純な形だけを使う。
/// </summary>
internal static class CardFaceRenderer
{
    // ------------------------------------------------------------------
    // 形（0〜100 の正方形に収まる Path データ）
    // ------------------------------------------------------------------

    private const string HeartData =
        "M50,92 C22,68 2,50 2,29 C2,13 14,2 28,2 C38,2 46,8 50,17 C54,8 62,2 72,2 C86,2 98,13 98,29 C98,50 78,68 50,92 Z";

    private const string DiamondData = "M50,2 L88,50 L50,98 L12,50 Z";

    private const string SpadeData =
        "M50,2 C38,20 2,40 2,63 C2,78 13,87 26,87 C35,87 42,83 46,76 C45,87 41,94 33,99 L67,99 C59,94 55,87 54,76 " +
        "C58,83 65,87 74,87 C87,87 98,78 98,63 C98,40 62,20 50,2 Z";

    // 3つの円（上・左・右）と軸。重なりは塗りつぶし規則 Nonzero（F1）で1つの形にする
    private const string ClubData =
        "F1 M28,27 A22,22 0 1 1 72,27 A22,22 0 1 1 28,27 Z " +
        "M3,58 A22,22 0 1 1 47,58 A22,22 0 1 1 3,58 Z " +
        "M53,58 A22,22 0 1 1 97,58 A22,22 0 1 1 53,58 Z " +
        "M45,55 C45,78 41,91 32,99 L68,99 C59,91 55,78 55,55 Z";

    // 絵札・ジョーカーの線アイコン（塗らずに線で描く）
    private const string CrownData = "M10,74 L10,30 L30,50 L50,16 L70,50 L90,30 L90,74 Z M10,86 L90,86";
    private const string TiaraData =
        "M14,76 L22,40 L38,58 L50,32 L62,58 L78,40 L86,76 Z M50,12 A6,6 0 1 1 49.9,12 Z M14,88 L86,88";
    private const string ShieldData = "M50,6 L88,20 L88,48 C88,72 70,87 50,96 C30,87 12,72 12,48 L12,20 Z";
    private const string StarData = "M50,4 L61,37 L96,37 L68,58 L79,92 L50,71 L21,92 L32,58 L4,37 L39,37 Z";

    // ------------------------------------------------------------------
    // ピップの配置（実際のトランプと同じ並び）
    // ------------------------------------------------------------------

    // 四隅のインデックス（左上 x4〜18・右下 x52〜66）と重ならないよう、左右の列は内側に寄せる
    private const double PipWidth = 10;
    private const double PipHeight = 11;
    private const double ColLeft = 25, ColCenter = 35, ColRight = 45;
    private const double RowTop = 22, RowBottom = 80;
    private const double CenterY = (RowTop + RowBottom) / 2;

    /// <summary>数字ごとのピップ中心（x, 上端0〜下端1 の割合）。</summary>
    private static readonly Dictionary<int, (double X, double Y)[]> PipLayouts = new()
    {
        [2] = new[] { (ColCenter, 0.0), (ColCenter, 1.0) },
        [3] = new[] { (ColCenter, 0.0), (ColCenter, 0.5), (ColCenter, 1.0) },
        [4] = new[] { (ColLeft, 0.0), (ColRight, 0.0), (ColLeft, 1.0), (ColRight, 1.0) },
        [5] = new[] { (ColLeft, 0.0), (ColRight, 0.0), (ColCenter, 0.5), (ColLeft, 1.0), (ColRight, 1.0) },
        [6] = new[] { (ColLeft, 0.0), (ColRight, 0.0), (ColLeft, 0.5), (ColRight, 0.5), (ColLeft, 1.0), (ColRight, 1.0) },
        [7] = new[] { (ColLeft, 0.0), (ColRight, 0.0), (ColCenter, 0.25), (ColLeft, 0.5), (ColRight, 0.5), (ColLeft, 1.0), (ColRight, 1.0) },
        [8] = new[]
        {
            (ColLeft, 0.0), (ColRight, 0.0), (ColCenter, 0.25), (ColLeft, 0.5), (ColRight, 0.5),
            (ColCenter, 0.75), (ColLeft, 1.0), (ColRight, 1.0),
        },
        [9] = new[]
        {
            (ColLeft, 0.0), (ColRight, 0.0), (ColLeft, 1 / 3.0), (ColRight, 1 / 3.0), (ColCenter, 0.5),
            (ColLeft, 2 / 3.0), (ColRight, 2 / 3.0), (ColLeft, 1.0), (ColRight, 1.0),
        },
        [10] = new[]
        {
            (ColLeft, 0.0), (ColRight, 0.0), (ColCenter, 1 / 6.0), (ColLeft, 1 / 3.0), (ColRight, 1 / 3.0),
            (ColLeft, 2 / 3.0), (ColRight, 2 / 3.0), (ColCenter, 5 / 6.0), (ColLeft, 1.0), (ColRight, 1.0),
        },
    };

    /// <summary>ピップの数（2〜10）。A・絵札・ジョーカーは 0。</summary>
    public static int PipCount(Card card)
        => card.IsJoker ? 0 : (int)card.Rank is >= 2 and <= 10 ? (int)card.Rank : 0;

    private static readonly SolidColorBrush JokerGoldBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xE2, 0xA8, 0x1C));

    /// <summary>カード表面の中央部分を描く。</summary>
    public static void Render(Canvas canvas, Card card, Brush suitBrush)
    {
        canvas.Children.Clear();

        if (card.IsJoker)
        {
            // ゴールドの星を、パープルの線で縁取る
            var box = new Viewbox { Width = 38, Height = 38, Stretch = Stretch.Uniform };
            var star = CreatePath(StarData, suitBrush, filled: false, strokeThickness: 5);
            star.Fill = JokerGoldBrush;
            box.Child = star;
            Canvas.SetLeft(box, 35 - 19);
            Canvas.SetTop(box, 51 - 19);
            canvas.Children.Add(box);
            return;
        }

        int pips = PipCount(card);
        if (pips > 0)
        {
            foreach (var (x, t) in PipLayouts[pips])
            {
                double y = RowTop + ((RowBottom - RowTop) * t);
                // 下半分は実際のトランプと同じく上下逆さまにする
                AddPip(canvas, card.Suit, suitBrush, x, y, PipWidth, PipHeight, rotate: y > CenterY + 0.5);
            }
            return;
        }

        if (card.Rank == Rank.Ace)
        {
            AddPip(canvas, card.Suit, suitBrush, 35, 51, 28, 30, rotate: false);
            return;
        }

        // 絵札：内枠＋線アイコン＋小さなスート
        var frame = new Border
        {
            Width = 44,
            Height = 70,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = suitBrush,
            Opacity = 0.55,
        };
        Canvas.SetLeft(frame, 13);
        Canvas.SetTop(frame, 16);
        canvas.Children.Add(frame);

        string icon = card.Rank switch
        {
            Rank.King => CrownData,
            Rank.Queen => TiaraData,
            _ => ShieldData,
        };
        AddOutlineIcon(canvas, icon, suitBrush, 35, card.Rank == Rank.Jack ? 47 : 45, 28);
        if (card.Rank == Rank.Jack)
        {
            // 盾の中にスートを描く
            AddPip(canvas, card.Suit, suitBrush, 35, 46, 10, 11, rotate: false);
        }
        AddPip(canvas, card.Suit, suitBrush, 35, 71, 9, 10, rotate: false);
    }

    /// <summary>四隅のインデックス用の小さなスート（ジョーカーは星の線画）。</summary>
    public static UIElement CreateIndexSuit(Card card, Brush brush)
    {
        if (card.IsJoker)
        {
            return CreatePath(StarData, brush, filled: false, strokeThickness: 10);
        }
        return CreatePath(SuitData(card.Suit), brush, filled: true, strokeThickness: 0);
    }

    private static string SuitData(Suit suit) => suit switch
    {
        Suit.Heart => HeartData,
        Suit.Diamond => DiamondData,
        Suit.Club => ClubData,
        _ => SpadeData,
    };

    private static void AddPip(Canvas canvas, Suit suit, Brush brush, double centerX, double centerY, double width, double height, bool rotate)
    {
        var box = new Viewbox
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            Child = CreatePath(SuitData(suit), brush, filled: true, strokeThickness: 0),
        };
        if (rotate)
        {
            box.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            box.RenderTransform = new RotateTransform { Angle = 180 };
        }
        Canvas.SetLeft(box, centerX - (width / 2));
        Canvas.SetTop(box, centerY - (height / 2));
        canvas.Children.Add(box);
    }

    private static void AddOutlineIcon(Canvas canvas, string data, Brush brush, double centerX, double centerY, double size)
    {
        var box = new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = CreatePath(data, brush, filled: false, strokeThickness: 6),
        };
        Canvas.SetLeft(box, centerX - (size / 2));
        Canvas.SetTop(box, centerY - (size / 2));
        canvas.Children.Add(box);
    }

    /// <summary>
    /// 形を 0〜100 の座標系の Path として作る（Viewbox で拡大縮小する）。
    /// Geometry は要素間で共有できないため、毎回文字列から作る。
    /// </summary>
    private static Path CreatePath(string data, Brush brush, bool filled, double strokeThickness)
    {
        var path = new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Width = 100,
            Height = 100,
            Stretch = Stretch.None,
        };
        if (filled)
        {
            path.Fill = brush;
        }
        else
        {
            path.Stroke = brush;
            path.StrokeThickness = strokeThickness;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }
        return path;
    }
}
