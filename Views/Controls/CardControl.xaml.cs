using System;
using DTTS.Common;
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

    public static readonly DependencyProperty IsKeyboardCursorProperty =
        DependencyProperty.Register(nameof(IsKeyboardCursor), typeof(bool), typeof(CardControl), new PropertyMetadata(false, OnIsKeyboardCursorChanged));

    public event EventHandler<Card>? CardClicked;
    public event EventHandler<Card>? CardDoubleClicked;

    private static ICardSkinManager? _skinManager;
    private bool _isPointerOver;

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

    /// <summary>キーボード操作のカーソルがこのカードにあるか（枠で示す）。</summary>
    public bool IsKeyboardCursor
    {
        get => (bool)GetValue(IsKeyboardCursorProperty);
        set => SetValue(IsKeyboardCursorProperty, value);
    }

    private static void OnIsKeyboardCursorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            control.KeyboardCursorOverlay.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public CardControl()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    /// <summary>
    /// ポップイン登場アニメーションを再生します。
    /// </summary>
    public void PopIn(int delayMs = 0)
    {
        CompositionAnimationHelper.AnimateCardPopIn(this, delayMs);
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
            control.SelectionOverlay.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;

            // Composition API による Spring 物理アニメーション（60fps）
            float targetY = sel ? -16f : (control._isPointerOver ? -4f : 0f);
            CompositionAnimationHelper.AnimateSpringTranslationY(control, targetY, dampingRatio: 0.62f, periodMs: 200f);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        if (!IsFacedDown)
        {
            CompositionAnimationHelper.AnimateSpringScale(this, 1.05f, dampingRatio: 0.7f, periodMs: 200f);
            if (!IsSelected)
            {
                CompositionAnimationHelper.AnimateSpringTranslationY(this, -5f, dampingRatio: 0.7f, periodMs: 200f);
            }
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        if (!IsFacedDown)
        {
            CompositionAnimationHelper.AnimateSpringScale(this, 1.0f, dampingRatio: 0.7f, periodMs: 220f);
            float targetY = IsSelected ? -16f : 0f;
            CompositionAnimationHelper.AnimateSpringTranslationY(this, targetY, dampingRatio: 0.65f, periodMs: 220f);
        }
    }

    private static void OnCanPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            bool can = (bool)e.NewValue;
            control.DisabledOverlay.Visibility = can ? Visibility.Collapsed : Visibility.Visible;
            CompositionAnimationHelper.AnimateOpacity(control, can ? 1.0f : 0.55f, 150);
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
        var suitBrush = GetSuitBrush(card.Suit);
        double rankFontSize = rankStr.Length >= 2 ? 10 : 12; // 「10」「JK」は2文字なので少し小さく

        TopRankText.Text = rankStr;
        TopRankText.FontSize = rankFontSize;
        TopRankText.Foreground = suitBrush;
        TopSuitBox.Child = CardFaceRenderer.CreateIndexSuit(card, suitBrush);

        BottomRankText.Text = rankStr;
        BottomRankText.FontSize = rankFontSize;
        BottomRankText.Foreground = suitBrush;
        BottomSuitBox.Child = CardFaceRenderer.CreateIndexSuit(card, suitBrush);

        // 中央：数字と同じ数のピップ・エース・絵札・ジョーカー
        CardFaceRenderer.Render(FaceCanvas, card, suitBrush);
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

    private static Brush GetSuitBrush(Suit suit) => suit switch
    {
        Suit.Heart or Suit.Diamond => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 85, 95)),
        Suit.Joker => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 195, 60)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 230, 235, 245))
    };
}

