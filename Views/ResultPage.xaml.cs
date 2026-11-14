using System;
using DTTS.Common;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DTTS.Views;

public sealed partial class ResultPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public ResultViewModel ViewModel { get; }

    private bool _isDialogOpen;

    public ResultPage()
    {
        ViewModel = App.Services.GetRequiredService<ResultViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    /// <summary>戦績のリセットは取り消せないので、確認してから消す。</summary>
    private async void OnClearHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen)
        {
            return;
        }
        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "戦績のリセット",
                Content = "これまでの対戦履歴と通算成績をすべて削除します。元に戻すことはできません。よろしいですか？",
                PrimaryButtonText = "削除する",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Result.ClearHistory", ex);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    // ===== x:Bind 用の表示ヘルパー =====

    /// <summary>分布の棒の長さ（最多の階級を 1 とした割合）。0 回でも少しだけ見せて、棒の位置がわかるようにする。</summary>
    public static double Ratio(int count, int max)
        => count <= 0 ? 0.02 : Math.Clamp((double)count / Math.Max(1, max), 0.04, 1.0);

    public static string CountText(int count) => $"{count}回";

    public static string StreakText(int streak) => streak > 0 ? $"{streak}連勝中" : "1位の割合";

    private static SolidColorBrush Solid(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));

    /// <summary>順位の色（対戦卓の階級バッジとそろえる：1位＝ゴールド、2位＝シルバー、3位＝ブロンズ、4位＝ダークレッド）。</summary>
    public static Brush PlaceBackground(int place) => place switch
    {
        1 => Solid(0xE9, 0xB9, 0x3A),
        2 => Solid(0xC5, 0xCB, 0xD6),
        3 => Solid(0xB0, 0x70, 0x42),
        4 => Solid(0x7E, 0x22, 0x2C),
        _ => Solid(0x4A, 0x55, 0x68),
    };

    public static Brush PlaceBorder(int place) => place switch
    {
        1 => Solid(0xFF, 0xDD, 0x7A),
        2 => Solid(0xEC, 0xEF, 0xF4),
        3 => Solid(0xD6, 0x95, 0x63),
        4 => Solid(0xB0, 0x3A, 0x46),
        _ => Solid(0x66, 0x72, 0x88),
    };

    public static Brush PlaceForeground(int place) => place switch
    {
        1 => Solid(0x2B, 0x1E, 0x00),
        2 => Solid(0x1F, 0x24, 0x2E),
        3 => Solid(0xFF, 0xF4, 0xEA),
        4 => Solid(0xFF, 0xE8, 0xEA),
        _ => Solid(0xE6, 0xEA, 0xF2),
    };
}
