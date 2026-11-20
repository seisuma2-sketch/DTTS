using System;
using DTTS.Common;
using DTTS.Core.Models;
using DTTS.ViewModels;
using DTTS.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DTTS.Views;

public sealed partial class TitlePage : Page
{
    public TitleViewModel ViewModel { get; }

    /// <summary>フッターに表示するバージョン（パッケージのマニフェストの値）。</summary>
    public string VersionText { get; } = $"v{GetPackageVersion()} • Offline Ready";

    /// <summary>扇に並べるカード（左から）。</summary>
    private static readonly string[] FanCards = { "C10", "DJ", "HQ", "SK", "SA", "JK0" };

    public TitlePage()
    {
        ViewModel = App.Services.GetRequiredService<TitleViewModel>();
        InitializeComponent();
        BuildCardFan();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // タイトルでは落ち着いた曲（対戦画面から戻ったときは、緊張の曲からフェードで切り替わる）
            App.Services.GetRequiredService<DTTS.Services.Audio.IAudioService>().PlayBgm(DTTS.Services.Audio.BgmTrack.Lounge);
            await ViewModel.LoadSummaryAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("TitlePage.OnPageLoaded", ex);
        }
    }

    /// <summary>
    /// トランプが扇状に広がるグラフィック。カードの下端中央を共通の要とし、少しずつ角度を変えて重ねる。
    /// 表示後に1枚ずつ弾むように現れる。
    /// </summary>
    private void BuildCardFan()
    {
        const double cardWidth = 96, cardHeight = 140;
        const double pivotX = 190, pivotY = 230; // 扇の要（カード下端より少し下）
        double step = 13;
        double start = -step * (FanCards.Length - 1) / 2;
        for (int i = 0; i < FanCards.Length; i++)
        {
            var card = new CardControl
            {
                Card = Card.Parse(FanCards[i]),
                Width = cardWidth,
                Height = cardHeight,
                IsHitTestVisible = false,
            };
            double left = pivotX - cardWidth / 2;
            double top = pivotY - cardHeight - 40;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            card.RenderTransform = new RotateTransform
            {
                Angle = start + step * i,
                CenterX = cardWidth / 2,
                CenterY = pivotY - top, // 要の位置（カード座標系）
            };
            FanCanvas.Children.Add(card);
        }

        Loaded += (_, _) =>
        {
            try
            {
                for (int i = 0; i < FanCanvas.Children.Count; i++)
                {
                    CompositionAnimationHelper.AnimateCardPopIn(FanCanvas.Children[i], 120 + i * 70);
                }
            }
            catch (Exception ex)
            {
                // 演出の失敗でタイトル画面を止めない
                App.WriteCrashLog("TitleFan", ex);
            }
        };
    }

    private static string GetPackageVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (System.InvalidOperationException)
        {
            // パッケージ化されずに起動した場合（パッケージ ID がない）
            return typeof(TitlePage).Assembly.GetName().Version?.ToString(3) ?? "?";
        }
    }

    private void OnButtonPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            CompositionAnimationHelper.AnimateSpringScale(el, 1.025f, dampingRatio: 0.72f, periodMs: 180f);
            CompositionAnimationHelper.AnimateSpringTranslationY(el, -2f, dampingRatio: 0.72f, periodMs: 180f);
        }
    }

    private void OnButtonPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            CompositionAnimationHelper.AnimateSpringScale(el, 1.0f, dampingRatio: 0.72f, periodMs: 200f);
            CompositionAnimationHelper.AnimateSpringTranslationY(el, 0f, dampingRatio: 0.72f, periodMs: 200f);
        }
    }
}
