using System;
using DTTS.Core.Enums;
using DTTS.Core.Models;
using DTTS.Services.Skin;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DTTS.Views.Controls;

public sealed partial class CardControl : UserControl
{
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register(nameof(Card), typeof(Card), typeof(CardControl), new PropertyMetadata(null, OnCardChanged));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(CardControl), new PropertyMetadata(false, OnIsSelectedChanged));

    public static readonly DependencyProperty CanPlayProperty =
        DependencyProperty.Register(nameof(CanPlay), typeof(bool), typeof(CardControl), new PropertyMetadata(true, OnCanPlayChanged));

    public static readonly DependencyProperty IsFacedDownProperty =
        DependencyProperty.Register(nameof(IsFacedDown), typeof(bool), typeof(CardControl), new PropertyMetadata(false, OnIsFacedDownChanged));

    public event EventHandler<Card>? CardClicked;
    public event EventHandler<Card>? CardDoubleClicked;

    private static ICardSkinManager? _skinManager;

    public static void InitializeSkinManager(ICardSkinManager skinManager)
    {
        _skinManager = skinManager;
    }

    public Card? Card
    {
        get => (Card?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool CanPlay
    {
        get => (bool)GetValue(CanPlayProperty);
        set => SetValue(CanPlayProperty, value);
    }

    public bool IsFacedDown
    {
        get => (bool)GetValue(IsFacedDownProperty);
        set => SetValue(IsFacedDownProperty, value);
    }

    public CardControl()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private static void OnCardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control) control.UpdateVisuals();
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            bool sel = (bool)e.NewValue;
            control.CardTranslate.Y = sel ? -16 : 0;
            control.SelectionOverlay.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnCanPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            bool can = (bool)e.NewValue;
            control.DisabledOverlay.Visibility = can ? Visibility.Collapsed : Visibility.Visible;
            control.Opacity = can ? 1.0 : 0.6;
        }
    }

    private static void OnIsFacedDownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control) control.UpdateVisuals();
    }

    public void UpdateVisuals()
    {
        if (IsFacedDown)
        {
            BackSurface.Visibility = Visibility.Visible;
            VectorSurface.Visibility = Visibility.Collapsed;
            SkinImage.Visibility = Visibility.Collapsed;

            var backImg = _skinManager?.GetBackImage();
            if (backImg != null)
            {
                BackSkinImage.Source = backImg;
                BackSkinImage.Visibility = Visibility.Visible;
            }
            else
            {
                BackSkinImage.Visibility = Visibility.Collapsed;
            }
            return;
        }

        BackSurface.Visibility = Visibility.Collapsed;

        if (Card == null)
        {
            VectorSurface.Visibility = Visibility.Collapsed;
            SkinImage.Visibility = Visibility.Collapsed;
            return;
        }

        var card = Card;

        // 1. スキン画像取得を試行
        var skinSource = _skinManager?.GetCardImage(card);
        if (skinSource != null)
        {
            SkinImage.Source = skinSource;
            SkinImage.Visibility = Visibility.Visible;
            VectorSurface.Visibility = Visibility.Collapsed;
            return;
        }

        // 2. ベクター描画フォールバック
        SkinImage.Visibility = Visibility.Collapsed;
        VectorSurface.Visibility = Visibility.Visible;

        string rankStr = GetRankString(card);
        string suitStr = GetSuitString(card.Suit);
        var suitBrush = GetSuitBrush(card.Suit);

        TopRankText.Text = rankStr;
        TopRankText.Foreground = suitBrush;
        TopSuitText.Text = suitStr;
        TopSuitText.Foreground = suitBrush;

        CenterSuitText.Text = card.IsJoker ? "★" : suitStr;
        CenterSuitText.Foreground = suitBrush;

        BottomRankText.Text = rankStr;
        BottomRankText.Foreground = suitBrush;
        BottomSuitText.Text = suitStr;
        BottomSuitText.Foreground = suitBrush;
    }

    private void OnSkinImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 読み込み失敗時はベクター描画にフォールバック
        SkinImage.Visibility = Visibility.Collapsed;
        VectorSurface.Visibility = Visibility.Visible;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Card != null && !IsFacedDown)
        {
            CardClicked?.Invoke(this, Card);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Card != null && !IsFacedDown)
        {
            CardDoubleClicked?.Invoke(this, Card);
        }
    }

    private static string GetRankString(Card card)
    {
        if (card.IsJoker) return "JK";
        return card.Rank switch
        {
            Rank.Ace => "A",
            Rank.Jack => "J",
            Rank.Queen => "Q",
            Rank.King => "K",
            _ => ((int)card.Rank).ToString()
        };
    }

    private static string GetSuitString(Suit suit) => suit switch
    {
        Suit.Spade => "♠",
        Suit.Heart => "♥",
        Suit.Diamond => "♦",
        Suit.Club => "♣",
        Suit.Joker => "★",
        _ => "?"
    };

    private static Brush GetSuitBrush(Suit suit) => suit switch
    {
        Suit.Heart or Suit.Diamond => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 85, 95)),
        Suit.Joker => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 195, 60)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 230, 235, 245))
    };
}

