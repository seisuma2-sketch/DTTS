using System;
using DTTS.Core.Enums;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DTTS.Views.Controls;

public sealed partial class PlayerPanelControl : UserControl
{
    public PlayerPanelControl()
    {
        InitializeComponent();
    }

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
        HandCountText.Text = $"{handCount}枚";
        ScoreText.Text = (score >= 0 ? $"+{score}" : score.ToString()) + "pt";

        // 階級テキストと色
        RankText.Text = rank switch
        {
            PlayerRank.Daifugo => "大富豪",
            PlayerRank.Fugo => "富豪",
            PlayerRank.Heimin => "平民",
            PlayerRank.Hinmin => "貧民",
            PlayerRank.Daihinmin => "大貧民",
            _ => "平民"
        };

        RankBadge.Background = rank switch
        {
            PlayerRank.Daifugo => new SolidColorBrush(ColorHelper.FromArgb(255, 235, 180, 40)),
            PlayerRank.Fugo => new SolidColorBrush(ColorHelper.FromArgb(255, 60, 150, 240)),
            PlayerRank.Heimin => new SolidColorBrush(ColorHelper.FromArgb(255, 110, 120, 140)),
            PlayerRank.Hinmin => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 100, 80)),
            PlayerRank.Daihinmin => new SolidColorBrush(ColorHelper.FromArgb(255, 140, 60, 60)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 110, 120, 140))
        };

        // 状態ステータス
        if (finishPlace.HasValue)
        {
            StatusText.Text = $"{finishPlace.Value}位であがり";
            StatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 100, 230, 150));
            StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(60, 50, 180, 90));
            RootBorder.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 180, 100));
        }
        else if (isThinking)
        {
            StatusText.Text = "考え中...";
            StatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 90));
            StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 180, 40));
            RootBorder.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 180, 40));
        }
        else if (isTurn)
        {
            StatusText.Text = "ターン";
            StatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 100, 180, 255));
            StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(60, 60, 140, 240));
            RootBorder.BorderBrush = (Brush)Application.Current.Resources["AppAccentBrush"];
        }
        else if (isPassed)
        {
            StatusText.Text = "パス";
            StatusText.Foreground = (Brush)Application.Current.Resources["AppTextMutedBrush"];
            StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(40, 60, 65, 80));
            RootBorder.BorderBrush = (Brush)Application.Current.Resources["AppBorderSubtleBrush"];
        }
        else
        {
            StatusText.Text = "待機中";
            StatusText.Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
            StatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(40, 50, 55, 70));
            RootBorder.BorderBrush = (Brush)Application.Current.Resources["AppBorderSubtleBrush"];
        }
    }
}

