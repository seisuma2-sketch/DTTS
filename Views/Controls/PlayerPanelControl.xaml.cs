using System;
using DTTS.Core.Enums;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DTTS.Views.Controls;

public sealed partial class PlayerPanelControl : UserControl
{
    /// <summary>裏向きミニカードを最大何枚まで並べるか（それ以上は枚数の数字で示す）。2段×10枚。</summary>
    private const int MaxMiniBacks = 20;

    /// <summary>1段に並べる枚数（5枚ずつのまとまり2つ）。</summary>
    private const int BacksPerRow = 10;

    /// <summary>まとまり1つの枚数。まとまりの間に隙間を空けて、ぱっと数えやすくする。</summary>
    private const int BacksPerGroup = 5;

    private const double BackWidth = 14;
    private const double BackHeight = 20;

    /// <summary>重なり（隣のカードがどれだけ見えるか＝8px）。</summary>
    private const double BackOverlap = -6;

    /// <summary>5枚のまとまりの間の隙間。</summary>
    private const double GroupGap = 4;

    /// <summary>残りがこの枚数以下になったら、枚数の表示を目立たせる（あがりが近い）。</summary>
    private const int FewCardsThreshold = 3;

    private int _shownBacks = -1;

    public PlayerPanelControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 手札を裏向きのミニカード束で見せるか（相手プレイヤー用）。自分の欄では手札そのものが見えているので出さない。
    /// </summary>
    public bool ShowCardBacks { get; set; } = true;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Color AccentColor()
        => Application.Current.Resources.TryGetValue("AppAccentColor", out var c) && c is Color color
            ? color
            : ColorHelper.FromArgb(255, 0x3E, 0x8E, 0xF7);

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => new(ColorHelper.FromArgb(a, r, g, b));

    /// <summary>階級の色（大富豪＝ゴールド、富豪＝シルバー、平民＝スレート、貧民＝ブロンズ、大貧民＝ダークレッド）。</summary>
    private static (Brush Background, Brush Border, Brush Foreground) RankColors(PlayerRank rank) => rank switch
    {
        PlayerRank.Daifugo => (Solid(255, 0xE9, 0xB9, 0x3A), Solid(255, 0xFF, 0xDD, 0x7A), Solid(255, 0x2B, 0x1E, 0x00)),
        PlayerRank.Fugo => (Solid(255, 0xC5, 0xCB, 0xD6), Solid(255, 0xEC, 0xEF, 0xF4), Solid(255, 0x1F, 0x24, 0x2E)),
        PlayerRank.Hinmin => (Solid(255, 0xB0, 0x70, 0x42), Solid(255, 0xD6, 0x95, 0x63), Solid(255, 0xFF, 0xF4, 0xEA)),
        PlayerRank.Daihinmin => (Solid(255, 0x7E, 0x22, 0x2C), Solid(255, 0xB0, 0x3A, 0x46), Solid(255, 0xFF, 0xE8, 0xEA)),
        _ => (Solid(255, 0x4A, 0x55, 0x68), Solid(255, 0x66, 0x72, 0x88), Solid(255, 0xE6, 0xEA, 0xF2)),
    };

    public void SetPlayerInfo(
        string name,
        int handCount,
        PlayerRank rank,
        int score,
        bool isTurn,
        bool isThinking,
        bool isPassed,
        int? finishPlace)
    {
        PlayerNameText.Text = name;
        HandCountText.Text = handCount.ToString();
        UpdateHandCountLook(finishPlace.HasValue ? 0 : handCount, finishPlace.HasValue);
        ScoreText.Text = (score >= 0 ? $"+{score}" : score.ToString()) + "pt";
        UpdateMiniBacks(finishPlace.HasValue ? 0 : handCount);

        RankText.Text = rank switch
        {
            PlayerRank.Daifugo => "大富豪",
            PlayerRank.Fugo => "富豪",
            PlayerRank.Heimin => "平民",
            PlayerRank.Hinmin => "貧民",
            PlayerRank.Daihinmin => "大貧民",
            _ => "平民"
        };
        var (rankBg, rankBorder, rankFg) = RankColors(rank);
        RankBadge.Background = rankBg;
        RankBadge.BorderBrush = rankBorder;
        RankText.Foreground = rankFg;

        bool active = !finishPlace.HasValue && (isTurn || isThinking);
        ApplyTurnGlow(active);

        // 状態ステータス
        if (finishPlace.HasValue)
        {
            SetStatus($"{finishPlace.Value}位で確定", Solid(255, 100, 230, 150), Solid(60, 50, 180, 90));
            RootBorder.BorderBrush = Solid(255, 60, 180, 100);
            TurnBadge.Visibility = Visibility.Collapsed;
        }
        else if (isThinking)
        {
            SetStatus("考え中...", Solid(255, 255, 210, 110), Solid(60, 255, 180, 40));
            RootBorder.BorderBrush = Res("AppAccentBrush");
            ShowTurnBadge("手番");
        }
        else if (isTurn)
        {
            SetStatus("あなたの番", Res("AppAccentBrush"), Res("AppAccentSubtleBrush"));
            RootBorder.BorderBrush = Res("AppAccentBrush");
            ShowTurnBadge("手番");
        }
        else if (isPassed)
        {
            SetStatus("パス", Res("AppTextMutedBrush"), Solid(40, 60, 65, 80));
            RootBorder.BorderBrush = Solid(255, 0x2E, 0x36, 0x46);
            TurnBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetStatus("待機中", Res("AppTextSecondaryBrush"), Solid(40, 50, 55, 70));
            RootBorder.BorderBrush = Solid(255, 0x2E, 0x36, 0x46);
            TurnBadge.Visibility = Visibility.Collapsed;
        }

        // 相手が考えている間は「考え中」、自分の番は「あなたの番」と分けて表示する
        if (isTurn && !isThinking && !finishPlace.HasValue && !ShowCardBacks)
        {
            StatusText.Text = "あなたの番";
        }
        else if (isTurn && !isThinking && !finishPlace.HasValue)
        {
            StatusText.Text = "手番";
        }
    }

    private void SetStatus(string text, Brush foreground, Brush background)
    {
        StatusText.Text = text;
        StatusText.Foreground = foreground;
        StatusDot.Fill = foreground;
        StatusBadge.Background = background;
    }

    private void ShowTurnBadge(string text)
    {
        TurnBadgeText.Text = text;
        TurnBadgeText.Foreground = Res("AppOnAccentBrush");
        TurnBadge.Background = Res("AppAccentBrush");
        TurnBadge.Visibility = Visibility.Visible;
    }

    /// <summary>手番のパネルをテーマカラーで発光させる（外側ほど薄い二重の光の輪）。</summary>
    private void ApplyTurnGlow(bool on)
    {
        var accent = AccentColor();
        TurnGlowOuter.BorderBrush = new SolidColorBrush(Color.FromArgb(46, accent.R, accent.G, accent.B));
        TurnGlowInner.BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B));
        TurnGlowOuter.Opacity = on ? 1 : 0;
        TurnGlowInner.Opacity = on ? 1 : 0;
        RootBorder.BorderThickness = new Thickness(on ? 2 : 1.5);
    }

    /// <summary>残り枚数の表示（あがりが近いときは目立たせる）と、読み上げ用の名前。</summary>
    private void UpdateHandCountLook(int count, bool finished)
    {
        bool few = ShowCardBacks && !finished && count > 0 && count <= FewCardsThreshold;
        HandCountPill.Background = few ? Solid(70, 0xFF, 0xB0, 0x3A) : Solid(46, 0xFF, 0xFF, 0xFF);
        HandCountPill.BorderBrush = few ? Solid(200, 0xFF, 0xC4, 0x5A) : Solid(64, 0xFF, 0xFF, 0xFF);
        HandCountText.Foreground = few ? Solid(255, 0xFF, 0xD5, 0x80) : Res("AppTextPrimaryBrush");
        string label = $"残り {count} 枚";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HandCountPill, label);
        ToolTipService.SetToolTip(HandCountPill, label);
    }

    /// <summary>
    /// 残り枚数に応じて、裏向きミニカードを並べる。1枚ずつの形が見えるよう少しずつ重ね、
    /// 5枚ごとに隙間を空ける（10枚を超えたら2段目へ）。白い縁取りで重なっていても枚数を数えられる。
    /// </summary>
    private void UpdateMiniBacks(int count)
    {
        int n = ShowCardBacks ? Math.Clamp(count, 0, MaxMiniBacks) : 0;
        MiniBackRows.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n == _shownBacks)
        {
            return;
        }
        _shownBacks = n;
        MiniBackRows.Children.Clear();
        var accent = AccentColor();
        StackPanel? row = null;
        for (int i = 0; i < n; i++)
        {
            int inRow = i % BacksPerRow;
            if (inRow == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal };
                MiniBackRows.Children.Add(row);
            }
            double left = inRow == 0 ? 0 : inRow % BacksPerGroup == 0 ? GroupGap : BackOverlap;
            row!.Children.Add(CreateBack(accent, left));
        }
    }

    private static Border CreateBack(Color accent, double leftMargin) => new()
    {
        Width = BackWidth,
        Height = BackHeight,
        Margin = new Thickness(leftMargin, 0, 0, 0),
        CornerRadius = new CornerRadius(2.5),
        // 白い縁取り：重なった隣のカードとの境目がはっきり見える
        BorderThickness = new Thickness(1.2),
        BorderBrush = Solid(255, 0xEE, 0xF2, 0xF8),
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = ColorHelper.FromArgb(255, 0x34, 0x4A, 0x70), Offset = 0 },
                new GradientStop { Color = ColorHelper.FromArgb(255, 0x1A, 0x24, 0x38), Offset = 1 },
            },
        },
        // 裏面の模様：テーマカラーの内枠（トランプの裏らしく見せる）
        Child = new Border
        {
            Margin = new Thickness(1.5),
            CornerRadius = new CornerRadius(1.2),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
            Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)),
        },
    };
}
