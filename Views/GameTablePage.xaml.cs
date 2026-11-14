using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Core.Models;
using DTTS.ViewModels;
using DTTS.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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
    public Brush PrismBrushHorizontal { get; } = CreateGradient(new Windows.Foundation.Point(0, 0.5), new Windows.Foundation.Point(1, 0.5), 255);

    /// <summary>四辺を走る光（縦辺用）。</summary>
    public Brush PrismBrushVertical { get; } = CreateGradient(new Windows.Foundation.Point(0.5, 0), new Windows.Foundation.Point(0.5, 1), 255);

    /// <summary>斜めに横切る光の帯（両端は透明）。</summary>
    public Brush AuroraBrush { get; } = CreateAuroraBrush();

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
        if (e.PropertyName == nameof(GameTableViewModel.IsRoundResultOpen) && ViewModel.IsRoundResultOpen)
        {
            HideCutIn();
            HideBomberVanish();
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

    private const double HandCardWidth = 70;
    private const double HandPaddingX = 20;
    private const double HandMaxSpacing = -16; // 余裕があるときの重なり
    private const double HandMinSpacing = -56; // 1枚あたり 14px 見える。これ以上重ねると左上の数字が読めなくなる（はみ出す分はスクロール）
    private bool _keyboardCursorVisible;

    private void OnHandAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHandSpacing();

    private void OnHumanHandCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateHandSpacing();
        UpdateKeyboardCursor();
    }

    private void UpdateHandSpacing()
    {
        if (HumanHandItemsControl.ItemsPanelRoot is not StackPanel panel) return;
        int count = ViewModel.HumanHand.Count;
        double available = HandScrollViewer.ActualWidth - HandPaddingX * 2;
        double spacing = HandMaxSpacing;
        if (count > 1 && available > 0)
        {
            double fit = (available - count * HandCardWidth) / (count - 1);
            spacing = Math.Clamp(fit, HandMinSpacing, HandMaxSpacing);
        }
        if (Math.Abs(panel.Spacing - spacing) > 0.5)
        {
            panel.Spacing = spacing;
        }
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
        CompositionAnimationHelper.AnimateFlushOut(FieldCardsContainer);
    }

    private void OnHandDealtTriggered(object? sender, EventArgs e)
    {
        AnimateHandDeal();
    }

    private void OnFieldCardsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            // 場にカードが出されたときのポップイン演出
            DispatcherQueue.TryEnqueue(() =>
            {
                for (int i = 0; i < FieldCardsItemsControl.Items.Count; i++)
                {
                    if (FieldCardsItemsControl.ContainerFromIndex(i) is ContentPresenter cp)
                    {
                        CompositionAnimationHelper.AnimateCardPopIn(cp, i * 40);
                    }
                }
            });
        }
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
        _cutInTimer?.Stop();
        _bomberTimer?.Stop();
        _finishTimer?.Stop();
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
        await ViewModel.InitializeAsync(_navParams);
        UpdateAllPanels();
        UpdateHandSpacing();
        Focus(FocusState.Programmatic);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateAllPanels();
    }

    private void UpdateAllPanels()
    {
        var p0 = ViewModel.Player0;
        Player0Panel.SetPlayerInfo(p0.Name, p0.HandCount, p0.Rank, p0.TotalScore, p0.IsTurn, p0.IsThinking, p0.IsPassed, p0.FinishPlace);

        var p1 = ViewModel.Player1;
        Player1Panel.SetPlayerInfo(p1.Name, p1.HandCount, p1.Rank, p1.TotalScore, p1.IsTurn, p1.IsThinking, p1.IsPassed, p1.FinishPlace);
        Player1SlimPanel.SetPlayerInfo(p1.Name, p1.HandCount, p1.Rank, p1.TotalScore, p1.IsTurn, p1.IsThinking, p1.IsPassed, p1.FinishPlace);

        var p2 = ViewModel.Player2;
        Player2Panel.SetPlayerInfo(p2.Name, p2.HandCount, p2.Rank, p2.TotalScore, p2.IsTurn, p2.IsThinking, p2.IsPassed, p2.FinishPlace);
        Player2SlimPanel.SetPlayerInfo(p2.Name, p2.HandCount, p2.Rank, p2.TotalScore, p2.IsTurn, p2.IsThinking, p2.IsPassed, p2.FinishPlace);

        var p3 = ViewModel.Player3;
        Player3Panel.SetPlayerInfo(p3.Name, p3.HandCount, p3.Rank, p3.TotalScore, p3.IsTurn, p3.IsThinking, p3.IsPassed, p3.FinishPlace);
        Player3SlimPanel.SetPlayerInfo(p3.Name, p3.HandCount, p3.Rank, p3.TotalScore, p3.IsTurn, p3.IsThinking, p3.IsPassed, p3.FinishPlace);
    }

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

    private void OnCardDoubleClicked(object? sender, Card card)
    {
        if (sender is CardControl ctrl && ctrl.DataContext is CardViewModel vm)
        {
            vm.IsSelected = true;
            ViewModel.PlaySelectedCardsCommand.Execute(null);
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
        }
    }
}
