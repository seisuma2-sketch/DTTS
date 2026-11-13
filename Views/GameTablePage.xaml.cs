using System;
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

    public GameTablePage()
    {
        ViewModel = App.Services.GetRequiredService<GameTableViewModel>();
        InitializeComponent();

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
        _cutInTimer?.Stop();
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
