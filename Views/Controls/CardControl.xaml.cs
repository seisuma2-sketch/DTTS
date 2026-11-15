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

    public static readonly DependencyProperty IsLegalSelectionProperty =
        DependencyProperty.Register(nameof(IsLegalSelection), typeof(bool), typeof(CardControl), new PropertyMetadata(false, OnIsLegalSelectionChanged));

    public event EventHandler<Card>? CardClicked;
    public event EventHandler<Card>? CardDoubleClicked;

    /// <summary>右クリック・長押し：同じ数字のカードをまとめて選びたい。</summary>
    public event EventHandler<Card>? SameRankRequested;

    /// <summary>選んでいるカードがそのまま出せる組になっているか（外側を光らせる）。</summary>
    public bool IsLegalSelection
    {
        get => (bool)GetValue(IsLegalSelectionProperty);
        set => SetValue(IsLegalSelectionProperty, value);
    }

    private static void OnIsLegalSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            bool on = (bool)e.NewValue;
            control.LegalGlow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                CompositionAnimationHelper.AnimateOpacity(control.LegalGlow, 0.9f, 160);
            }
        }
    }

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
        ApplyPlayableTone(CanPlay);

        // 画像の読み込みに失敗したらベクター描画へ切り替える（表示中のカードだけが購読する）
        Loaded += (_, _) =>
        {
            if (_skinManager is not null)
            {
                _skinManager.ImageLoadFailed -= OnSkinImageLoadFailed;
                _skinManager.ImageLoadFailed += OnSkinImageLoadFailed;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_skinManager is not null)
            {
                _skinManager.ImageLoadFailed -= OnSkinImageLoadFailed;
            }
        };
    }

    private void OnSkinImageLoadFailed(object? sender, Card failed)
    {
        // 失敗した画像はスキン管理側で記録済みなので、描き直すと次の候補（標準ジョーカー→ベクター）になる
        if (Card is { } card && card.Id == failed.Id)
        {
            UpdateVisuals();
        }
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

    // カード表面の色（出せるとき：明るいアイボリー／出せないとき：少し落ち着いたグレー。どちらも数字は必ず読める）
    private static readonly Windows.UI.Color FaceTop = ColorHelper.FromArgb(255, 0xFB, 0xFC, 0xFE);
    private static readonly Windows.UI.Color FaceBottom = ColorHelper.FromArgb(255, 0xE8, 0xEC, 0xF2);
    private static readonly Windows.UI.Color DimFaceTop = ColorHelper.FromArgb(255, 0xCC, 0xD3, 0xDE);
    private static readonly Windows.UI.Color DimFaceBottom = ColorHelper.FromArgb(255, 0xC4, 0xCC, 0xD8);

    private static void OnCanPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control)
        {
            control.ApplyPlayableTone((bool)e.NewValue);
        }
    }

    /// <summary>
    /// 出せないカードは表面の地色だけを落ち着いたグレーに変える。不透明度は 1.0 のままにして、
    /// 重なった下のカードが透けないようにする（数字・スートの色は変えないので常に読める）。
    /// </summary>
    private void ApplyPlayableTone(bool canPlay)
    {
        FaceStopTop.Color = canPlay ? FaceTop : DimFaceTop;
        FaceStopBottom.Color = canPlay ? FaceBottom : DimFaceBottom;
        UpdateImageDim();
    }

    /// <summary>画像の表面（スキン・標準ジョーカー）は地色が見えないので、出せないときは薄いグレーを重ねて示す。</summary>
    private void UpdateImageDim()
    {
        bool imageFace = !IsFacedDown
            && (SkinImage.Visibility == Visibility.Visible || JokerArtLayer.Visibility == Visibility.Visible);
        ImageDimOverlay.Visibility = imageFace && !CanPlay ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnIsFacedDownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl control) control.UpdateVisuals();
    }

    public void UpdateVisuals()
    {
        UpdateFace();
        UpdateImageDim();
    }

    private void UpdateFace()
    {
        JokerArtLayer.Visibility = Visibility.Collapsed;
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

        SkinImage.Visibility = Visibility.Collapsed;

        // 2. 標準のジョーカーの絵柄（スキンパックにジョーカーの画像がないとき）
        if (card.IsJoker && _skinManager?.GetBuiltInJokerImage(card) is { } jokerArt)
        {
            JokerArtBrush.ImageSource = jokerArt;
            JokerArtLayer.Visibility = Visibility.Visible;
            VectorSurface.Visibility = Visibility.Collapsed;
            return;
        }

        // 3. ベクター描画フォールバック
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
        UpdateImageDim();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Card == null || IsFacedDown)
        {
            return;
        }
        // 右クリックは「同じ数字をまとめて選ぶ」（1枚だけの選択切り替えにはしない）
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            SameRankRequested?.Invoke(this, Card);
            e.Handled = true;
            return;
        }
        CardClicked?.Invoke(this, Card);
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        // タッチ・ペンの長押しも「同じ数字をまとめて選ぶ」
        if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started && Card != null && !IsFacedDown)
        {
            SameRankRequested?.Invoke(this, Card);
            e.Handled = true;
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

    /// <summary>明るいカード表面に映える色：♠♣ は深いチャコール、♥♦ はクリムゾン、ジョーカーはパープル（星はゴールド）。</summary>
    private static Brush GetSuitBrush(Suit suit) => suit switch
    {
        Suit.Heart or Suit.Diamond => new SolidColorBrush(ColorHelper.FromArgb(255, 0xD9, 0x38, 0x48)),
        Suit.Joker => new SolidColorBrush(ColorHelper.FromArgb(255, 0x6D, 0x3F, 0xC8)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 0x1E, 0x22, 0x2B))
    };
}

