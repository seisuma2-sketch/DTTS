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

public sealed partial class GameTablePage : Page, IBackRequestHandler
{
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
    }

    private void OnCutInTriggered(object? sender, CutInEventArgs e)
    {
        CutInTitleText.Text = e.Title;
        CutInSubText.Text = e.SubTitle;
        CutInDetailText.Text = e.Detail;

        var color = ThemeManager.ParseHexColor(e.AccentColorHex);
        var brush = new SolidColorBrush(color);
        CutInTitleText.Foreground = brush;
        CutInBorder.BorderBrush = brush;

        CompositionAnimationHelper.AnimateCutIn(CutInOverlay);
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
                    if (_focusedCardIndex >= 0 && _focusedCardIndex < ViewModel.HumanHand.Count)
                    {
                        var vm = ViewModel.HumanHand[_focusedCardIndex];
                        ViewModel.ToggleCardSelectionCommand.Execute(vm);
                    }
                    e.Handled = true;
                }
                break;

            case VirtualKey.Left or VirtualKey.A:
                if (ViewModel.HumanHand.Count > 0)
                {
                    _focusedCardIndex = Math.Max(0, _focusedCardIndex - 1);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Right or VirtualKey.D:
                if (ViewModel.HumanHand.Count > 0)
                {
                    _focusedCardIndex = Math.Min(ViewModel.HumanHand.Count - 1, _focusedCardIndex + 1);
                    e.Handled = true;
                }
                break;
        }
    }
}
