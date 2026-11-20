using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Settings;
using DTTS.ViewModels;
using DTTS.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace DTTS.Views;

public sealed partial class GameTablePage : Page, IBackRequestHandler, IPageKeyHandler
{
    /// <inheritdoc />
    public void HandlePageKey(KeyRoutedEventArgs e)
    {
        OnPageKeyDown(this, e);
        if (e.Handled && !_isDetached && !_isLeaveDialogOpen)
        {
            // 外れていたフォーカスをページへ戻し、以降のキーは直接ページで受ける
            Focus(FocusState.Programmatic);
        }
    }

    public GameTableViewModel ViewModel { get; }

    public bool TrueValue => true;
    public bool FalseValue => false;

    private int _focusedCardIndex = 0;

    // ------------------------------------------------------------------
    // 全画面演出（場流れ：Prism Flash / 12ボンバー・革命：Aurora Sweep）の色
    // ------------------------------------------------------------------

    private static readonly Windows.UI.Color AuroraCyan = Windows.UI.Color.FromArgb(255, 0x22, 0xD3, 0xEE);
    private static readonly Windows.UI.Color AuroraViolet = Windows.UI.Color.FromArgb(255, 0x8B, 0x5C, 0xF6);

    /// <summary>四辺を走る光（横辺用）：テーマカラー → シアン → バイオレット。</summary>
    public LinearGradientBrush PrismBrushHorizontal { get; } = CreateGradient(new Windows.Foundation.Point(0, 0.5), new Windows.Foundation.Point(1, 0.5), 255);

    /// <summary>四辺を走る光（縦辺用）。</summary>
    public LinearGradientBrush PrismBrushVertical { get; } = CreateGradient(new Windows.Foundation.Point(0.5, 0), new Windows.Foundation.Point(0.5, 1), 255);

    /// <summary>斜めに横切る光の帯（両端は透明）。</summary>
    public LinearGradientBrush AuroraBrush { get; } = CreateAuroraBrush();

    private void UpdateAccentSweepBrushes()
    {
        var accent = AccentColor();
        PrismBrushHorizontal.GradientStops[0].Color = WithAlpha(accent, 255);
        PrismBrushVertical.GradientStops[0].Color = WithAlpha(accent, 255);
        AuroraBrush.GradientStops[3].Color = WithAlpha(accent, 70);
        AuroraBrush.GradientStops[4].Color = WithAlpha(accent, 0);
    }

    private static Windows.UI.Color AccentColor()
        => Application.Current.Resources.TryGetValue("AppAccentBrush", out var b) && b is SolidColorBrush scb
            ? scb.Color
            : Windows.UI.Color.FromArgb(255, 0x3B, 0x82, 0xF6);

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, byte a) => Windows.UI.Color.FromArgb(a, c.R, c.G, c.B);

    private static LinearGradientBrush CreateGradient(Windows.Foundation.Point start, Windows.Foundation.Point end, byte alpha)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AccentColor(), alpha), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AuroraCyan, alpha), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AuroraViolet, alpha), Offset = 1.0 });
        return brush;
    }

    private static LinearGradientBrush CreateAuroraBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AuroraViolet, 0), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AuroraViolet, 70), Offset = 0.25 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AuroraCyan, 95), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AccentColor(), 70), Offset = 0.75 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(AccentColor(), 0), Offset = 1.0 });
        return brush;
    }

    private void OnSweepTriggered(object? sender, GameTableViewModel.SweepKind kind)
    {
        // ラウンド結果の表示中は出さない（結果を読む邪魔をしない）
        if (ViewModel.IsRoundResultOpen) return;
        try
        {
            if (kind == GameTableViewModel.SweepKind.AuroraSweep)
            {
                CompositionAnimationHelper.AnimateAuroraSweep(AuroraBand, SweepLayer.ActualWidth);
            }
            else
            {
                CompositionAnimationHelper.AnimatePrismFlash(PrismTop, PrismRight, PrismBottom, PrismLeft);
            }
        }
        catch (Exception ex)
        {
            // 演出の失敗で対戦を止めない
            App.WriteCrashLog("Sweep", ex);
        }
    }

    // ------------------------------------------------------------------
    // 12ボンバーの数字ボタン（x:Bind 関数）
    // ------------------------------------------------------------------

    public static Brush RankBackground(bool selected)
        => (Brush)Application.Current.Resources[selected ? "AppAccentBrush" : "AppBackgroundDeepBrush"];

    public static Brush RankForeground(bool selected)
        => (Brush)Application.Current.Resources[selected ? "AppOnAccentBrush" : "AppTextPrimaryBrush"];

    public static Thickness CursorThickness(bool isCursor) => new(isCursor ? 2.5 : 0);

    private void OnRankOptionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RankOptionViewModel option })
        {
            ViewModel.ToggleRankCommand.Execute(option);
        }
    }

    private void OnToggleHistoryClicked(object sender, RoutedEventArgs e)
    {
        bool show = PlayHistoryPanel.Visibility != Visibility.Visible;
        PlayHistoryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            CompositionAnimationHelper.AnimatePopIn(PlayHistoryPanel, 160);
        }
    }

    private void OnReactionFlyoutOpening(object? sender, object e)
    {
        ReactionList.ItemsSource = ViewModel.QuickReactionOptions;
    }

    private void OnQuickReactionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string code)
        {
            ViewModel.SendReactionCommand.Execute(code);
            ReactionFlyout.Hide();
            if (!_isDetached) Focus(FocusState.Programmatic);
        }
    }

    private void OnSendCustomReactionClicked(object sender, RoutedEventArgs e)
    {
        SendCustomReactionAndClose();
    }

    private void OnCustomReactionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            SendCustomReactionAndClose();
            e.Handled = true;
        }
    }

    private void SendCustomReactionAndClose()
    {
        ViewModel.SendCustomReactionCommand.Execute(null);
        ReactionFlyout.Hide();
        if (!_isDetached) Focus(FocusState.Programmatic);
    }

    private void OnFieldDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        FieldContainerTransform.X = 0;
        FieldContainerTransform.Y = 0;
    }

    public static string ResultPositionLabel(int position) => $"{position}位";

    public static Brush ResultRowBackground(bool isMe)
        => isMe
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(44, 62, 142, 247))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255));

    public GameTablePage()
    {
        ViewModel = App.Services.GetRequiredService<GameTableViewModel>();
        InitializeComponent();
        ViewModel.SweepTriggered += OnSweepTriggered;

        ViewModel.Player0.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player1.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player2.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player3.PropertyChanged += OnPlayerPropertyChanged;

        // アニメーション・カットイン演出イベント購読
        ViewModel.CutInTriggered += OnCutInTriggered;
        ViewModel.FieldFlushedTriggered += OnFieldFlushedTriggered;
        ViewModel.HandDealtTriggered += OnHandDealtTriggered;
        ViewModel.FieldCards.CollectionChanged += OnFieldCardsCollectionChanged;
        ViewModel.HumanHand.CollectionChanged += OnHumanHandCollectionChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.BomberVanishTriggered += OnBomberVanishTriggered;
        ViewModel.FinishCelebrationTriggered += OnFinishCelebrationTriggered;
        ViewModel.ReactionTriggered += OnReactionTriggered;
        ViewModel.CardsFlightTriggered += OnCardsFlightTriggered;
        ThemeManager.ThemeChanged += OnThemeChanged;
        SizeChanged += OnPageSizeChanged;
    }

    private bool? _lastCompactLayout;
    private bool _isWindowMinimized;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isDetached) return;
        bool compact = OpponentRow.Visibility == Visibility.Visible;
        if (_lastCompactLayout != compact)
        {
            _lastCompactLayout = compact;
            UpdateAllPanels();
        }
    }

    /// <summary>ウィンドウ最小化時に手番脈動の無限ループアニメーションを停止し、復帰時に再開する。</summary>
    internal void SetWindowMinimized(bool minimized)
    {
        if (_isWindowMinimized == minimized || _isDetached) return;
        _isWindowMinimized = minimized;
        if (minimized)
        {
            Player0Panel.PauseTurnGlow();
            LeftSeatPanel.PauseTurnGlow();
            TopSeatPanel.PauseTurnGlow();
            RightSeatPanel.PauseTurnGlow();
            LeftSeatSlimPanel.PauseTurnGlow();
            TopSeatSlimPanel.PauseTurnGlow();
            RightSeatSlimPanel.PauseTurnGlow();
        }
        else
        {
            UpdateAllPanels();
        }
    }

    private void OnThemeChanged(object? sender, ThemeColor color)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDetached)
            {
                UpdateAccentSweepBrushes();
            }
        });
    }

    // ------------------------------------------------------------------
    // 12ボンバー：消えた数字を画面中央で弱い順に1つずつ見せる
    // ------------------------------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bomberTimer;
    private IReadOnlyList<BomberVanishItem>? _bomberItems;
    private int _bomberIndex;
    private double _bomberStepMs;

    private void OnBomberVanishTriggered(object? sender, BomberVanishEventArgs e)
    {
        // ラウンド結果の表示中は出さない（結果を読む邪魔をしない）
        if (ViewModel.IsRoundResultOpen || e.Items.Count == 0) return;

        HideCutIn(); // 同じ中央に重ならないように
        _bomberItems = e.Items;
        _bomberIndex = 0;
        // 数字が多いときは1枚あたりを短くして、全体が長くなりすぎないようにする
        _bomberStepMs = e.Items.Count > 4 ? 560 : 860;
        BomberHeaderText.Text = $"12ボンバー  {e.DeclarerName} の宣言";
        BomberOverlay.Visibility = Visibility.Visible;

        if (_bomberTimer is null)
        {
            _bomberTimer = DispatcherQueue.CreateTimer();
            _bomberTimer.IsRepeating = true;
            _bomberTimer.Tick += (_, _) => ShowNextBomberChip();
        }
        _bomberTimer.Stop();
        _bomberTimer.Interval = TimeSpan.FromMilliseconds(_bomberStepMs);
        ShowNextBomberChip();
        _bomberTimer.Start();
    }

    /// <summary>次の数字を表示する。すべて見せ終えたら片付ける。</summary>
    private void ShowNextBomberChip()
    {
        if (_bomberItems is null || _bomberIndex >= _bomberItems.Count)
        {
            HideBomberVanish();
            return;
        }
        var item = _bomberItems[_bomberIndex++];
        bool isJoker = item.Rank == DTTS.Core.Enums.Rank.Joker;
        BomberChipRank.Text = item.Label;
        BomberChipRank.FontSize = isJoker ? 54 : 78;
        BomberChipCornerTop.Text = isJoker ? "★" : item.Label;
        BomberChipCornerBottom.Text = isJoker ? "★" : item.Label;
        BomberCountText.Text = item.DiscardedCount > 0
            ? $"{(isJoker ? "ジョーカー" : item.Label)}  {item.DiscardedCount}枚 消滅"
            : $"{(isJoker ? "ジョーカー" : item.Label)}  持っている人なし";
        try
        {
            // 次の数字へ切り替わる直前に消え終わるよう、わずかに短くする
            CompositionAnimationHelper.AnimateBomberChip(BomberChipHost, BomberStrike, item.DiscardedCount > 0, _bomberStepMs - 40);
        }
        catch (Exception ex)
        {
            // 演出の失敗で対戦を止めない
            App.WriteCrashLog("BomberVanish", ex);
            HideBomberVanish();
        }
    }

    private void HideBomberVanish()
    {
        _bomberTimer?.Stop();
        _bomberItems = null;
        BomberOverlay.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // あがりの演出（柔らかい光・細い線・順位リボン）
    // ------------------------------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _finishTimer;

    // 1位：プラチナゴールド、2位：プラチナ、3位：ローズブロンズ（どれも淡い色。テーマカラーと合わせて使う）
    private static readonly Windows.UI.Color[] FinishPlaceColors =
    {
        Windows.UI.Color.FromArgb(255, 0xF6, 0xE9, 0xC4),
        Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xF0),
        Windows.UI.Color.FromArgb(255, 0xE9, 0xC6, 0xAD),
    };

    private void OnFinishCelebrationTriggered(object? sender, FinishCelebrationEventArgs e)
    {
        try
        {
            var platinum = FinishPlaceColors[Math.Clamp(e.Place, 1, FinishPlaceColors.Length) - 1];
            var accent = AccentColor();

            FinishPlaceText.Text = $"{e.Place}位";
            FinishPlaceText.FontSize = e.IsSelf ? 46 : 36;
            FinishPlaceText.Foreground = new SolidColorBrush(platinum);
            FinishNameText.Text = e.IsSelf ? "あがり！" : $"{e.PlayerName}  あがり";
            FinishNameText.FontSize = e.IsSelf ? 24 : 19;
            FinishRankText.Text = e.RankTitle;

            // 光：中心はプラチナ、外へ向かってテーマカラーに溶けて透明になる
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop { Color = WithAlpha(platinum, (byte)(e.IsSelf ? 120 : 84)), Offset = 0.0 });
            glow.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, (byte)(e.IsSelf ? 64 : 44)), Offset = 0.45 });
            glow.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 0), Offset = 1.0 });
            FinishGlow.Fill = glow;
            FinishGlow.Width = e.IsSelf ? 760 : 620;
            FinishGlow.Height = e.IsSelf ? 280 : 230;

            // 線：両端は透明、テーマカラーからプラチナへ
            var line = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
            };
            line.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 0), Offset = 0.0 });
            line.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 220), Offset = 0.25 });
            line.GradientStops.Add(new GradientStop { Color = platinum, Offset = 0.5 });
            line.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 220), Offset = 0.75 });
            line.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 0), Offset = 1.0 });
            FinishLineTop.Fill = line;
            FinishLineBottom.Fill = line;
            FinishLineTop.Width = FinishLineBottom.Width = e.IsSelf ? 460 : 380;

            PlaceFinishOverlay();
            FinishOverlay.Visibility = Visibility.Visible;
            FinishOverlay.UpdateLayout(); // 中心点の計算に実サイズを使う

            double durationMs = e.IsSelf ? 2600 : 2000;
            CompositionAnimationHelper.AnimateFinishCelebration(FinishGlow, FinishLineTop, FinishLineBottom, FinishRibbon, durationMs);

            if (_finishTimer is null)
            {
                _finishTimer = DispatcherQueue.CreateTimer();
                _finishTimer.IsRepeating = false;
                _finishTimer.Tick += (_, _) => HideFinishCelebration();
            }
            _finishTimer.Stop();
            _finishTimer.Interval = TimeSpan.FromMilliseconds(durationMs + 60);
            _finishTimer.Start();
        }
        catch (Exception ex)
        {
            // 演出の失敗で対戦を止めない
            App.WriteCrashLog("FinishCelebration", ex);
            HideFinishCelebration();
        }
    }

    /// <summary>
    /// あがりの演出の位置。対戦中は場札の少し上、ラウンド結果の表示中は結果の上（画面上部）に出す。
    /// 最後のあがりはラウンド結果と同時になるため、結果に隠れないようにする。
    /// </summary>
    private void PlaceFinishOverlay()
    {
        if (ViewModel.IsRoundResultOpen)
        {
            FinishOverlay.VerticalAlignment = VerticalAlignment.Top;
            FinishOverlay.Margin = new Thickness(0, 18, 0, 0);
        }
        else
        {
            FinishOverlay.VerticalAlignment = VerticalAlignment.Center;
            FinishOverlay.Margin = new Thickness(0, 0, 0, 170);
        }
    }

    private void HideFinishCelebration()
    {
        _finishTimer?.Stop();
        FinishOverlay.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // カットイン演出（ラウンド結果の表示時には即座に消す）
    // ------------------------------------------------------------------

    /// <summary>カットインの表示時間。アニメーション（約1.2秒）が終わったあとで確実に非表示へ戻す。</summary>
    private static readonly TimeSpan CutInLifetime = TimeSpan.FromMilliseconds(1400);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cutInTimer;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameTableViewModel.SeatLayoutVersion))
        {
            UpdateAllPanels(); // 席替え・初回の座席順の確定：左・上・右に出す人を並べ直す
        }

        if (e.PropertyName is nameof(GameTableViewModel.IsRevolution) or nameof(GameTableViewModel.RevolutionBgColor))
        {
            UpdateTableBackground();
        }

        if (e.PropertyName == nameof(GameTableViewModel.IsRoundResultOpen) && ViewModel.IsRoundResultOpen)
        {
            HideCutIn();
            HideBomberVanish();
            FlightLayer.Children.Clear(); // 飛んでいる途中のカードの写しは、結果の上に残さない
            CompositionAnimationHelper.AnimatePopIn(RoundResultCard);
            // 最後のあがりの演出は消さず、結果の上（画面上部）へ移して最後まで見せる
            if (FinishOverlay.Visibility == Visibility.Visible)
            {
                PlaceFinishOverlay();
            }
        }

        // オーバーレイ（ラウンド結果・判断ダイアログ）が閉じると、その中のボタンにあったフォーカスが
        // ページの外へ外れることがある。キー操作を続けられるようページへ戻す
        if ((e.PropertyName == nameof(GameTableViewModel.IsRoundResultOpen) && !ViewModel.IsRoundResultOpen)
            || (e.PropertyName == nameof(GameTableViewModel.IsDecisionDialogOpen) && !ViewModel.IsDecisionDialogOpen))
        {
            if (!_isDetached && !_isLeaveDialogOpen)
            {
                Focus(FocusState.Programmatic);
            }
        }
    }

    private void HideCutIn()
    {
        _cutInTimer?.Stop();
        try
        {
            CompositionAnimationHelper.ResetCutIn(CutInOverlay);
        }
        catch (Exception ex)
        {
            // 演出の失敗で対戦を止めない（記録だけ残して非表示にする）
            App.WriteCrashLog("HideCutIn", ex);
            CutInOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCutInTriggered(object? sender, CutInEventArgs e)
    {
        // ラウンド結果を表示している間は演出を出さない（結果の裏に残らないように）
        if (ViewModel.IsRoundResultOpen) return;

        CutInTitleText.Text = e.Title;
        CutInSubText.Text = e.SubTitle;
        CutInDetailText.Text = e.Detail;

        var color = ThemeManager.ParseHexColor(e.AccentColorHex);
        var brush = new SolidColorBrush(color);
        CutInTitleText.Foreground = brush;
        CutInBorder.BorderBrush = brush;
        CutInIcon.Glyph = e.Glyph;
        CutInIcon.Foreground = brush;

        try
        {
            CompositionAnimationHelper.AnimateCutIn(CutInOverlay);
        }
        catch (Exception ex)
        {
            // 演出の失敗で対戦を止めない
            App.WriteCrashLog("AnimateCutIn", ex);
            CutInOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // 1回だけ発火するタイマーで、アニメーションの完了通知に頼らず必ず非表示へ戻す
        if (_cutInTimer is null)
        {
            _cutInTimer = DispatcherQueue.CreateTimer();
            _cutInTimer.IsRepeating = false;
            _cutInTimer.Tick += (_, _) => HideCutIn();
        }
        _cutInTimer.Stop();
        _cutInTimer.Interval = CutInLifetime;
        _cutInTimer.Start();
    }

    // ------------------------------------------------------------------
    // 手札の並べ方（重なり幅を表示幅と枚数に合わせて調整）とキーボードカーソル
    // ------------------------------------------------------------------

    private const double HandCardWidth = 84;
    private const double HandPaddingX = 20;
    private const double HandMaxSpacing = -20; // 余裕があるときの重なり（影の分も重ねる）
    private const double HandMinSpacing = -62; // 1枚あたり約 22px 見える。左上の数字とスートが読める限界（はみ出す分はスクロール）
    private bool _keyboardCursorVisible;

    private void OnHandAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHandSpacing();

    private void OnHumanHandCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateHandSpacing();
        UpdateKeyboardCursor();
        // 新しいカードの入れ物は描画後にできるので、その後でもう一度大きさをそろえる
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateHandSpacing);
    }

    /// <summary>手札が収まらないときに縮小できる下限（元の 84×122 に対する倍率）。</summary>
    private const double HandMinScale = 0.78;
    private const double HandCardHeight = 122;
    private double _handScale = 1.0;

    private void UpdateHandSpacing()
    {
        if (HumanHandItemsControl.ItemsPanelRoot is not StackPanel panel) return;
        int count = ViewModel.HumanHand.Count;
        double available = HandScrollViewer.ActualWidth - HandPaddingX * 2;

        // まず重なりを深くして収め、それでも足りなければカード自体を縮小する（狭いウィンドウ・コンパクト表示）
        double scale = 1.0;
        if (count > 1 && available > 0)
        {
            // 幅 w・最大の重なり時の必要幅 = count*w + (count-1)*(minSpacing) = w*(count + (count-1)*minSpacing/cardWidth)
            double ratio = count + (count - 1) * (HandMinSpacing / HandCardWidth);
            double needed = HandCardWidth * ratio;
            if (needed > available)
            {
                scale = Math.Clamp(available / needed, HandMinScale, 1.0);
            }
        }
        ApplyHandCardScale(scale);

        double width = HandCardWidth * scale;
        double spacing = HandMaxSpacing * scale;
        if (count > 1 && available > 0)
        {
            double fit = (available - count * width) / (count - 1);
            spacing = Math.Clamp(fit, HandMinSpacing * scale, HandMaxSpacing * scale);
        }
        if (Math.Abs(panel.Spacing - spacing) > 0.5)
        {
            panel.Spacing = spacing;
        }
    }

    /// <summary>手札のカードの大きさをそろえて変える（数字・スートの比率はカード側の Viewbox が保つ）。</summary>
    private void ApplyHandCardScale(double scale)
    {
        _handScale = scale;
        double w = Math.Round(HandCardWidth * scale), h = Math.Round(HandCardHeight * scale);
        for (int i = 0; i < ViewModel.HumanHand.Count; i++)
        {
            if (HumanHandItemsControl.ContainerFromIndex(i) is DependencyObject container
                && FindChild<CardControl>(container) is { } card
                && (Math.Abs(card.Width - w) > 0.5 || Math.Abs(card.Height - h) > 0.5))
            {
                card.Width = w;
                card.Height = h;
            }
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }
            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    private void UpdateKeyboardCursor()
    {
        var hand = ViewModel.HumanHand;
        if (hand.Count > 0)
        {
            _focusedCardIndex = Math.Clamp(_focusedCardIndex, 0, hand.Count - 1);
        }
        for (int i = 0; i < hand.Count; i++)
        {
            hand[i].IsKeyboardCursor = _keyboardCursorVisible && i == _focusedCardIndex;
        }
    }

    private void MoveKeyboardCursor(int delta)
    {
        _keyboardCursorVisible = true;
        _focusedCardIndex += delta;
        UpdateKeyboardCursor();
        if (HumanHandItemsControl.ContainerFromIndex(_focusedCardIndex) is FrameworkElement fe)
        {
            fe.StartBringIntoView();
        }
    }

    private void OnFieldFlushedTriggered(object? sender, EventArgs e)
    {
        // マット枠（FieldCardsContainer）自体は中央に固定し、中のカード一覧だけを流す
        CompositionAnimationHelper.AnimateFlushOut(FieldCardsItemsControl);
    }

    private void OnHandDealtTriggered(object? sender, EventArgs e)
    {
        AnimateHandDeal();
    }

    /// <summary>飛ばす途中のカード（場札の一覧が入れ替わっても、その場のポップインはしない）。</summary>
    private IReadOnlyList<Card>? _pendingFlight;

    /// <summary>場のカードの大きさ（場が流れた後に着地点を作るとき用。場の CardControl の既定の大きさ）。</summary>
    private const double FieldCardWidth = 84, FieldCardHeight = 122, FieldCardSpacing = -12;

    private void OnFieldCardsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null || _pendingFlight is not null)
        {
            return; // 出し札は CardsFlightTriggered 側で、席から飛ばして見せる
        }
        // 出し札の合図がない場札の変化（ゲストの画面の取り直しなど）は、その場でポップインする
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isDetached || _pendingFlight is not null) return;
            for (int i = 0; i < FieldCardsItemsControl.Items.Count; i++)
            {
                if (FieldCardsItemsControl.ContainerFromIndex(i) is ContentPresenter cp)
                {
                    CompositionAnimationHelper.AnimateCardPopIn(cp, i * 40);
                }
            }
        });
    }

    /// <summary>
    /// カードが出された：出したプレイヤーの席（自分なら手札）から場へ、カードを1枚ずつ滑らせる。
    /// 8切り・砂嵐などで場がすぐ流れた場合も、カードはいったん場に着地してから流れていく。
    /// </summary>
    private void OnCardsFlightTriggered(object? sender, CardsFlightEventArgs e)
    {
        if (_isDetached) return;
        var source = FlightSource(e.PlayerId); // 場札が入れ替わる前に、席の位置を測っておく
        if (source is null) return;
        _pendingFlight = e.Cards;
        // 場札の一覧が入れ替わってから写しが飛び立つまで、本物を一瞬でも見せない
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(FieldCardsItemsControl).Opacity = 0f;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RunFlight(e.Cards, source.Value));
    }

    private void RunFlight(IReadOnlyList<Card> cards, (Windows.Foundation.Point Point, float StartScale) source)
    {
        if (ReferenceEquals(_pendingFlight, cards)) _pendingFlight = null;
        var itemsVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(FieldCardsItemsControl);
        if (_isDetached || ViewModel.IsFastForwarding)
        {
            itemsVisual.Opacity = 1f;
            return;
        }
        FieldCardsItemsControl.UpdateLayout();
        var field = ViewModel.FieldCards;
        bool onField = cards.All(field.Contains);

        // 場が流れた後（8切りなど）の着地点：場のマットの中央に、場と同じ並びで置く
        var mat = FieldCardsContainer.TransformToVisual(FlightLayer).TransformPoint(
            new Windows.Foundation.Point(FieldCardsContainer.ActualWidth / 2, FieldCardsContainer.ActualHeight / 2));
        double rowWidth = cards.Count * FieldCardWidth + (cards.Count - 1) * FieldCardSpacing;

        for (int i = 0; i < cards.Count; i++)
        {
            ContentPresenter? real = onField ? FieldCardsItemsControl.ContainerFromIndex(field.IndexOf(cards[i])) as ContentPresenter : null;
            Windows.Foundation.Rect target;
            if (real is { ActualWidth: > 0 })
            {
                var o = real.TransformToVisual(FlightLayer).TransformPoint(new Windows.Foundation.Point(0, 0));
                target = new Windows.Foundation.Rect(o.X, o.Y, real.ActualWidth, real.ActualHeight);
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(real).Opacity = 0f; // 写しが着地するまで隠す
            }
            else
            {
                double x = mat.X - rowWidth / 2 + i * (FieldCardWidth + FieldCardSpacing);
                target = new Windows.Foundation.Rect(x, mat.Y - FieldCardHeight / 2, FieldCardWidth, FieldCardHeight);
                real = null;
            }
            FlyGhost(cards[i], target, source.Point, source.StartScale, i * 45, real);
        }
        itemsVisual.Opacity = 1f; // 本物はそれぞれ着地まで隠してある
    }

    /// <summary>
    /// カードの写しを画面の一番上の層（FlightLayer）に置いて、出どころから着地点まで滑らせる（場の枠は角丸で中身を切り抜くため）。
    /// 着地したら、本物が場にあれば写しと入れ替え、場がもう流れていれば少し見せてから流れるように消す。
    /// </summary>
    private void FlyGhost(Card card, Windows.Foundation.Rect target, Windows.Foundation.Point source, float startScale, int delayMs, ContentPresenter? real)
    {
        var ghost = new CardControl
        {
            Card = card,
            Width = target.Width,
            Height = target.Height,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ghost, target.X);
        Canvas.SetTop(ghost, target.Y);
        FlightLayer.Children.Add(ghost);
        ghost.UpdateLayout();

        var center = new Windows.Foundation.Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        var from = new System.Numerics.Vector3((float)(source.X - center.X), (float)(source.Y - center.Y), 0f);
        // 念のための後始末：着地の通知が来なくても（ウィンドウが裏に回った等）、写しを残さず本物を見せる
        var safety = DispatcherQueue.CreateTimer();
        safety.Interval = TimeSpan.FromMilliseconds(delayMs + 1800);
        safety.IsRepeating = false;
        _flightTimers.Add(safety);
        safety.Tick += (t, _) =>
        {
            t.Stop();
            _flightTimers.Remove(t);
            if (_isDetached) return;
            if (FlightLayer.Children.Remove(ghost) && real is not null)
            {
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(real).Opacity = 1f;
            }
        };
        safety.Start();

        CompositionAnimationHelper.AnimateCardFlight(ghost, from, startScale, delayMs, () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isDetached || !FlightLayer.Children.Contains(ghost)) return; // 後始末済み
                bool stillOnField = real is not null && real.XamlRoot is not null && ViewModel.FieldCards.Contains(card);
                if (stillOnField)
                {
                    FlightLayer.Children.Remove(ghost);
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(real!).Opacity = 1f;
                    return;
                }
                // 場はもう流れている：着地した姿を一瞬見せてから、場流れと同じ向きに流して消す
                var hold = DispatcherQueue.CreateTimer();
                hold.Interval = TimeSpan.FromMilliseconds(260);
                hold.IsRepeating = false;
                _flightTimers.Add(hold);
                hold.Tick += (t, _) =>
                {
                    t.Stop();
                    _flightTimers.Remove(t);
                    if (_isDetached) return;
                    CompositionAnimationHelper.AnimateFlushOut(ghost, () => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isDetached) FlightLayer.Children.Remove(ghost);
                    }));
                };
                hold.Start();
            });
        });
    }

    /// <summary>場へ飛ぶカードの出どころ（画面上の点と、出発時の大きさ）。分からなければ null（その場でポップイン）。</summary>
    private (Windows.Foundation.Point Point, float StartScale)? FlightSource(int? playerId)
    {
        if (playerId is not { } id) return null;
        FrameworkElement? element;
        float scale;
        bool compact = OpponentRow.Visibility == Visibility.Visible;
        switch (ViewModel.SlotOfPlayer(id))
        {
            case SeatLayout.Bottom:
                element = HandScrollViewer;
                scale = 0.9f;
                break;
            case SeatLayout.Left:
                element = compact ? LeftSeatSlimPanel : LeftSeatPanel;
                scale = 0.45f;
                break;
            case SeatLayout.Top:
                element = compact ? TopSeatSlimPanel : TopSeatPanel;
                scale = 0.45f;
                break;
            case SeatLayout.Right:
                element = compact ? RightSeatSlimPanel : RightSeatPanel;
                scale = 0.45f;
                break;
            default:
                return null;
        }
        if (element is null || element.ActualWidth <= 0) return null;
        var p = element.TransformToVisual(FlightLayer).TransformPoint(new Windows.Foundation.Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return (p, scale);
    }

    private void OnReactionTriggered(object? sender, ReactionEventArgs e)
    {
        if (_isDetached) return;
        bool compact = OpponentRow.Visibility == Visibility.Visible;
        PlayerPanelControl? panel = ViewModel.SlotOfPlayer(e.PlayerId) switch
        {
            SeatLayout.Bottom => Player0Panel, // 自分が送った定型リアクション
            SeatLayout.Left => compact ? LeftSeatSlimPanel : LeftSeatPanel,
            SeatLayout.Top => compact ? TopSeatSlimPanel : TopSeatPanel,
            SeatLayout.Right => compact ? RightSeatSlimPanel : RightSeatPanel,
            _ => null,
        };
        panel?.ShowReaction(e.Text, DTTS.Services.Presentation.NpcReactions.Lifetime);
    }

    private void AnimateHandDeal()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            for (int i = 0; i < HumanHandItemsControl.Items.Count; i++)
            {
                if (HumanHandItemsControl.ContainerFromIndex(i) is ContentPresenter cp)
                {
                    CompositionAnimationHelper.AnimateCardPopIn(cp, i * 35);
                }
            }
        });
    }

    private NetworkGameParams? _navParams;
    private bool _isLeaveDialogOpen;
    private bool _isDetached;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _flightTimers = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navParams = e.Parameter as NetworkGameParams;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // どの経路で画面を離れても（タイトル・戦績画面）対戦の後始末と購読解除を行う
        DetachFromViewModel();
        _ = ViewModel.ShutdownAsync();
    }

    /// <summary>ウィンドウ終了時に購読解除と戦績保存・ネットワーク切断の完了を待つ。</summary>
    internal Task PrepareForWindowCloseAsync()
    {
        DetachFromViewModel();
        return ViewModel.ShutdownAsync();
    }

    /// <summary>ViewModel・プレイヤー・場札コレクションへの購読をすべて解除する。</summary>
    private void DetachFromViewModel()
    {
        if (_isDetached) return;
        _isDetached = true;
        ViewModel.Player0.PropertyChanged -= OnPlayerPropertyChanged;
        ViewModel.Player1.PropertyChanged -= OnPlayerPropertyChanged;
        ViewModel.Player2.PropertyChanged -= OnPlayerPropertyChanged;
        ViewModel.Player3.PropertyChanged -= OnPlayerPropertyChanged;
        ViewModel.CutInTriggered -= OnCutInTriggered;
        ViewModel.FieldFlushedTriggered -= OnFieldFlushedTriggered;
        ViewModel.HandDealtTriggered -= OnHandDealtTriggered;
        ViewModel.FieldCards.CollectionChanged -= OnFieldCardsCollectionChanged;
        ViewModel.HumanHand.CollectionChanged -= OnHumanHandCollectionChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SweepTriggered -= OnSweepTriggered;
        ViewModel.BomberVanishTriggered -= OnBomberVanishTriggered;
        ViewModel.FinishCelebrationTriggered -= OnFinishCelebrationTriggered;
        ViewModel.ReactionTriggered -= OnReactionTriggered;
        ViewModel.CardsFlightTriggered -= OnCardsFlightTriggered;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        SizeChanged -= OnPageSizeChanged;
        Player0Panel.PauseTurnGlow();
        LeftSeatPanel.PauseTurnGlow();
        TopSeatPanel.PauseTurnGlow();
        RightSeatPanel.PauseTurnGlow();
        LeftSeatSlimPanel.PauseTurnGlow();
        TopSeatSlimPanel.PauseTurnGlow();
        RightSeatSlimPanel.PauseTurnGlow();
        _cutInTimer?.Stop();
        _bomberTimer?.Stop();
        _finishTimer?.Stop();
        foreach (var timer in _flightTimers)
        {
            timer.Stop();
        }
        _flightTimers.Clear();
    }

    // ------------------------------------------------------------------
    // タイトルへ戻る（← タイトル ボタン・Esc・Alt+←・マウスの戻るボタン）
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public void OnBackRequested() => _ = RequestLeaveAsync();

    private void OnTitleButtonClicked(object sender, RoutedEventArgs e) => _ = RequestLeaveAsync();

    /// <summary>対戦中なら確認してから、ラウンド結果表示中・ゲーム終了後はそのままタイトルへ戻る。</summary>
    private async Task RequestLeaveAsync()
    {
        if (_isLeaveDialogOpen || _isDetached) return;

        if (ViewModel.NeedsLeaveConfirmation)
        {
            _isLeaveDialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "対戦の中断",
                    Content = ViewModel.LeaveConfirmationMessage,
                    PrimaryButtonText = "中断して戻る",
                    CloseButtonText = "続ける",
                    DefaultButton = ContentDialogButton.Close,
                    RequestedTheme = ElementTheme.Dark,
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    Focus(FocusState.Programmatic);
                    return;
                }
            }
            finally
            {
                _isLeaveDialogOpen = false;
            }
        }

        await ViewModel.LeaveToTitleAsync();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync(_navParams);
            if (_isDetached) return;
            UpdateAllPanels();
            UpdateTableBackground();
            UpdateHandSpacing();
            Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("GameTablePage.OnPageLoaded", ex);
            ViewModel.StatusMessage = $"初期化エラー: {ex.Message}";
        }
    }

    private bool _panelUpdateQueued;

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_panelUpdateQueued) return;
        _panelUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _panelUpdateQueued = false;
            UpdateAllPanels();
        });
    }

    private void UpdateAllPanels()
    {
        bool compact = OpponentRow.Visibility == Visibility.Visible;
        _lastCompactLayout = compact;

        ShowOne(ViewModel.Player0, Player0Panel, visible: true);
        // 座席順で自分の次の人から 左 → 上 → 右（画面上の時計回り）。表示中のパネルだけ更新し、非表示側の無限アニメーションを止める
        ShowPair(ViewModel.SeatSlotPlayer(SeatLayout.Left), LeftSeatPanel, LeftSeatSlimPanel, compact);
        ShowPair(ViewModel.SeatSlotPlayer(SeatLayout.Top), TopSeatPanel, TopSeatSlimPanel, compact);
        ShowPair(ViewModel.SeatSlotPlayer(SeatLayout.Right), RightSeatPanel, RightSeatSlimPanel, compact);

        void ShowPair(PlayerViewModel p, PlayerPanelControl wide, PlayerPanelControl slim, bool isCompact)
        {
            ShowOne(p, wide, visible: !isCompact);
            ShowOne(p, slim, visible: isCompact);
        }

        void ShowOne(PlayerViewModel p, PlayerPanelControl panel, bool visible)
        {
            if (!visible)
            {
                panel.PauseTurnGlow();
                return;
            }
            panel.SetPlayerInfo(p.Name, p.HandCount, p.Rank, p.TotalScore, p.IsTurn, p.IsThinking, p.IsPassed, p.FinishPlace);
            panel.SetAvatar(p.AvatarGlyph, p.AvatarLabel, p.AvatarImagePath, p.AvatarAccentHex);
            if (_isWindowMinimized)
            {
                panel.PauseTurnGlow();
            }
        }
    }

    private void UpdateTableBackground()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.46),
            GradientOrigin = new Windows.Foundation.Point(0.5, 0.42),
            RadiusX = 0.72,
            RadiusY = 0.78,
        };

        if (ViewModel.IsRevolution && ViewModel.RevolutionBgColor != RevolutionBgColor.None)
        {
            var info = RevolutionBgColorInfo.Get(ViewModel.RevolutionBgColor);
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor(info.CenterHex), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor(info.MidHex), Offset = 0.38 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor(info.OuterHex), Offset = 0.78 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor(info.EdgeHex), Offset = 1 });
        }
        else
        {
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor("#FF223047"), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor("#FF1D2636"), Offset = 0.38 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor("#FF151B26"), Offset = 0.78 });
            brush.GradientStops.Add(new GradientStop { Color = ParseHexColor("#FF0E121A"), Offset = 1 });
        }

        // WinUI 3 では GradientStop.Color の書き換えだけでは再描画されないことがあるためブラシごと差し替える
        TableFeltBorder.Background = brush;
    }

    public static Brush TurnHighlightBorder(bool isMyTurn)
        => isMyTurn
            ? (Brush)Application.Current.Resources["AppAccentBrush"]
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public static Brush TurnHighlightBg(bool isMyTurn)
        => isMyTurn
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private CardViewModel? _draggingCard;

    private void OnHandCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (ViewModel.IsSpectating)
        {
            args.Cancel = true;
            return;
        }
        if (sender is FrameworkElement { DataContext: CardViewModel vm })
        {
            _draggingCard = vm;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(vm.Card.Id.ToString());
        }
    }

    private void OnHandCardDragOver(object sender, DragEventArgs e)
    {
        if (_draggingCard is not null && !ViewModel.IsSpectating)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
    }

    private void OnHandCardDrop(object sender, DragEventArgs e)
    {
        if (_draggingCard is null || ViewModel.IsSpectating) return;
        if (sender is FrameworkElement { DataContext: CardViewModel target })
        {
            int from = ViewModel.HumanHand.IndexOf(_draggingCard);
            int to = ViewModel.HumanHand.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to)
            {
                ViewModel.MoveHandCard(from, to);
            }
        }
        _draggingCard = null;
    }

    private void OnFieldDragOver(object sender, DragEventArgs e)
    {
        if (_draggingCard is not null && !ViewModel.IsSpectating)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "場に出す";
        }
    }

    private void OnFieldDrop(object sender, DragEventArgs e)
    {
        if (_draggingCard is null || ViewModel.IsSpectating) return;
        var dragged = _draggingCard;
        _draggingCard = null;

        // 複数選択中かつその中に含まれるなら選択カード全部、それ以外はドラッグした1枚を出す
        var selected = ViewModel.HumanHand.Where(c => c.IsSelected).Select(c => c.Card).ToList();
        var toPlay = (dragged.IsSelected && selected.Count > 0) ? selected : new List<Card> { dragged.Card };
        ViewModel.PlayCardsByDrag(toPlay);
    }

    private void OnFieldContainerManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        // トランプを置く場札エリアを中央付近で少し移動できるようにする
        FieldContainerTransform.X = Math.Clamp(FieldContainerTransform.X + e.Delta.Translation.X, -160, 160);
        FieldContainerTransform.Y = Math.Clamp(FieldContainerTransform.Y + e.Delta.Translation.Y, -60, 60);
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        byte a = 255;
        int offset = hex.Length == 9 ? 3 : 1;
        if (hex.Length == 9)
        {
            a = Convert.ToByte(hex.Substring(1, 2), 16);
        }
        byte r = Convert.ToByte(hex.Substring(offset, 2), 16);
        byte g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    private void OnSpectateTargetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int playerId })
        {
            ViewModel.SelectSpectatePlayerCommand.Execute(playerId);
        }
    }

    public static Brush SpectateButtonBg(bool isSelected)
    {
        if (isSelected)
        {
            return (Brush)Application.Current.Resources["AppAccentBrush"];
        }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0x1A, 0x20, 0x2C));
    }

    public static Brush SpectateButtonFg(bool isSelected)
    {
        if (isSelected)
        {
            return (Brush)Application.Current.Resources["AppOnAccentBrush"];
        }
        return (Brush)Application.Current.Resources["AppTextSecondaryBrush"];
    }

    public static Brush SpectateButtonBorder(bool isSelected)
    {
        if (isSelected)
        {
            return (Brush)Application.Current.Resources["AppAccentBrush"];
        }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    public static string SpectateButtonText(string name, int handCount)
        => $"{name} ({handCount}枚)";

    private void OnCardClicked(object? sender, Card card)
    {
        if (sender is CardControl ctrl && ctrl.DataContext is CardViewModel vm)
        {
            // マウス操作に切り替わったらキーボードカーソルは隠し、位置だけクリックしたカードに合わせる
            int index = ViewModel.HumanHand.IndexOf(vm);
            if (index >= 0) _focusedCardIndex = index;
            _keyboardCursorVisible = false;
            UpdateKeyboardCursor();

            // 選択の切り替えは ViewModel のコマンドに集約（選択音・出せるかの再計算もここで行う）
            ViewModel.ToggleCardSelectionCommand.Execute(vm);
        }
    }

    /// <summary>まとめて選ぶ候補（ペア・階段など）を押した：その組をまとめて選ぶ。</summary>
    private void OnSuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DTTS.Services.Presentation.PlaySuggestion suggestion })
        {
            ViewModel.ApplySuggestionCommand.Execute(suggestion);
            Focus(FocusState.Programmatic); // Enter で「出す」が効くよう、フォーカスを画面へ戻す
        }
    }

    /// <summary>カードの右クリック・長押し：同じ数字のカードをまとめて選ぶ。</summary>
    private void OnCardSameRankRequested(object? sender, Card card)
    {
        if (sender is CardControl { DataContext: CardViewModel vm })
        {
            ViewModel.SelectSameRankCommand.Execute(vm);
        }
    }

    private void OnCardDoubleClicked(object? sender, Card card)
    {
        if (ViewModel.IsCardSelectionMode || !ViewModel.IsMyTurn) return;
        if (sender is CardControl ctrl && ctrl.DataContext is CardViewModel vm && vm.CanPlay)
        {
            // ViewModel の選択切り替えコマンド経由で選択し、CanPlay を確実に最新化してから出す
            if (!vm.IsSelected)
            {
                ViewModel.ToggleCardSelectionCommand.Execute(vm);
            }
            if (ViewModel.CanPlay)
            {
                ViewModel.PlaySelectedCardsCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// キー操作。表示中の画面に応じて優先順位を切り替える：
    /// ラウンド結果 ＞ 判断ダイアログ ＞ 通常プレイ。処理したキーは Handled にして二重反応を防ぐ。
    /// </summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isLeaveDialogOpen || _isDetached) return;

        // Alt との組み合わせ（Alt+← の戻る操作など）はウィンドウのキーボードアクセラレータに任せる。
        // ここで ← を「カードのフォーカス移動」として処理済みにすると、アクセラレータが発火しなくなる
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        // --- 1. ラウンド結果・ゲーム終了 ---
        if (ViewModel.IsRoundResultOpen)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                case VirtualKey.Space:
                    if (ViewModel.CanAdvanceRound)
                    {
                        ViewModel.StartNextRoundCommand.Execute(null);
                    }
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                    OnBackRequested();
                    e.Handled = true;
                    break;
            }
            return;
        }

        // --- 1b. 自分があがった後：S で残りをスキップして結果へ ---
        if (e.Key == VirtualKey.S && ViewModel.CanSkipToResult)
        {
            ViewModel.SkipToResultCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // --- 2. 判断ダイアログ（革命選択・カード選択） ---
        if (ViewModel.IsDecisionDialogOpen)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    ViewModel.TryConfirmDecisionDialog();
                    e.Handled = true;
                    return;
                case VirtualKey.Space when ViewModel.IsRevolutionChoice:
                    ViewModel.TryConfirmDecisionDialog();
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    if (!ViewModel.TryCancelDecisionDialog())
                    {
                        OnBackRequested(); // カード選択中の Esc は中断確認へ
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.Back:
                    e.Handled = true; // ダイアログ中のパスは無効
                    return;
            }
            if (ViewModel.IsRankDeclaration)
            {
                // 12ボンバーの数字宣言：数字ボタンを ←/→（↑/↓ は1行＝7個分）で選び、Space で選ぶ・外す
                switch (e.Key)
                {
                    case VirtualKey.Left or VirtualKey.A:
                        ViewModel.MoveRankCursor(-1);
                        e.Handled = true;
                        break;
                    case VirtualKey.Right or VirtualKey.D:
                        ViewModel.MoveRankCursor(+1);
                        e.Handled = true;
                        break;
                    case VirtualKey.Up or VirtualKey.W:
                        ViewModel.MoveRankCursor(-7);
                        e.Handled = true;
                        break;
                    case VirtualKey.Down or VirtualKey.S:
                        ViewModel.MoveRankCursor(+7);
                        e.Handled = true;
                        break;
                    case VirtualKey.Space:
                        ViewModel.ToggleRankAtCursor();
                        e.Handled = true;
                        break;
                }
                return; // 宣言中は手札を操作しない
            }
            if (ViewModel.IsRevolutionChoice)
            {
                return; // 革命選択中は手札を操作しない
            }
            // カード選択中は下の Space・矢印キーで手札を選ぶ
        }

        // --- 3. 通常プレイ ---
        switch (e.Key)
        {
            case VirtualKey.Escape:
                OnBackRequested();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (ViewModel.CanPlay)
                {
                    ViewModel.PlaySelectedCardsCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Back:
                if (ViewModel.CanPass)
                {
                    ViewModel.PassCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Space:
                if (ViewModel.HumanHand.Count > 0)
                {
                    MoveKeyboardCursor(0); // カーソルを表示し、範囲内に収める
                    var vm = ViewModel.HumanHand[_focusedCardIndex];
                    ViewModel.ToggleCardSelectionCommand.Execute(vm);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Left or VirtualKey.A:
                if (ViewModel.HumanHand.Count > 0)
                {
                    MoveKeyboardCursor(-1);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Right or VirtualKey.D:
                if (ViewModel.HumanHand.Count > 0)
                {
                    MoveKeyboardCursor(+1);
                    e.Handled = true;
                }
                break;

            case VirtualKey.C:
                ViewModel.ClearSelectionCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
