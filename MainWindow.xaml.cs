using System;
using System.IO;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Services.Audio;
using DTTS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DTTS;

public sealed partial class MainWindow : Window
{
    private bool _isShuttingDown;
    private bool _isShutdownCompleted;
    private bool _isMinimized;
    private bool _isDeactivated;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DTTS - 4-Player Daifugo Tabletop Suite";
        ApplyDarkTitleBar();

        // ウィンドウ左上・タスクバーのアイコン（パッケージ内の Assets\WindowIcon.ico）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WindowIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // ウィンドウ終了時に表示中ページのクリーンアップと DI リソース破棄を完了させてから閉じる
        AppWindow.Closing += OnAppWindowClosing;
        // ウィンドウ最小化・非アクティブ時に AudioGraph・GPU 無限アニメーションを止めて消費電力を抑える
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnWindowActivated;

        // ナビゲーションサービスにルートFrameを設定
        var nav = App.Services.GetRequiredService<INavigationService>();
        nav.RootFrame = ContentFrame;
        // 最小化・非アクティブ中の画面遷移でも新しいページの無限アニメーションを確実に止める
        ContentFrame.Navigated += (_, _) => UpdatePageAnimationPowerState();

        // マウスの「戻る」ボタン（子要素が処理済みのクリックでも拾う）
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);

        // タイトル画面へ遷移
        nav.NavigateToTitle();
    }

    /// <summary>
    /// システムのタイトルバーをアプリの背景に馴染むダークなカスタムタイトルバー（AppTitleBar）に置き換える。
    /// 最小化・最大化・閉じるボタンも同じ配色にする。
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }
            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xE6, 0xEA, 0xF2);
            bar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0xFF, 0x6B, 0x73, 0x82);
            bar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
            bar.ButtonHoverForegroundColor = ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            bar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            bar.ButtonPressedForegroundColor = ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        }
        catch (Exception ex)
        {
            // タイトルバーの装飾に失敗しても、標準のタイトルバーのまま起動を続ける
            App.WriteCrashLog("MainWindow.TitleBar", ex);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        bool deactivated = args.WindowActivationState == WindowActivationState.Deactivated;
        if (_isDeactivated == deactivated) return;
        _isDeactivated = deactivated;
        UpdatePowerState();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        bool minimized = sender.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized;
        if (_isMinimized == minimized) return;
        _isMinimized = minimized;
        UpdatePowerState();
    }

    private void UpdatePowerState()
    {
        try
        {
            bool suspend = _isMinimized || _isDeactivated;
            App.Services.GetService<IAudioService>()?.SetSuspended(suspend);
            UpdatePageAnimationPowerState();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("MainWindow.PowerState", ex);
        }
    }

    private void UpdatePageAnimationPowerState()
    {
        try
        {
            bool suspendAnimations = _isMinimized || _isDeactivated;
            if (ContentFrame.Content is GameTablePage gamePage)
            {
                gamePage.SetWindowMinimized(suspendAnimations);
            }
            else if (ContentFrame.Content is LobbyPage lobbyPage)
            {
                lobbyPage.SetWindowMinimized(suspendAnimations);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("MainWindow.PowerState", ex);
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isShutdownCompleted) return;
        args.Cancel = true;
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try
        {
            Task pageCleanup = ContentFrame.Content switch
            {
                GameTablePage gamePage => gamePage.PrepareForWindowCloseAsync(),
                LobbyPage lobbyPage => lobbyPage.PrepareForWindowCloseAsync(),
                _ => Task.CompletedTask,
            };
            // 戦績保存や退出通知の送信を最大 2.5 秒まで待機する
            await Task.WhenAny(pageCleanup, Task.Delay(TimeSpan.FromMilliseconds(2500)));
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("MainWindow.Closing", ex);
        }
        finally
        {
            App.ShutdownServices();
            _isShutdownCompleted = true;
            Close();
        }
    }

    /// <summary>表示中のページへ戻る操作を伝える（タイトル画面など未対応のページでは何もしない）。</summary>
    private void RequestBack()
    {
        if (ContentFrame.Content is IBackRequestHandler handler)
        {
            handler.OnBackRequested();
        }
    }

    /// <summary>
    /// ページが処理しなかった Esc・キーボードの「戻る」キーをここで受ける（ゲーム画面は Esc を自前で処理して Handled にする）。
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.GoBack)
        {
            RequestBack();
            e.Handled = true;
            return;
        }

        // ページの外（フレーム自体など）にフォーカスが外れていても、ゲーム画面のキー操作を効かせる。
        // ページ内から上がってきたキーはページ側で処理済みなので、二重に処理しない
        if (ContentFrame.Content is IPageKeyHandler keyHandler
            && ContentFrame.Content is DependencyObject page
            && !IsWithin(e.OriginalSource as DependencyObject, page))
        {
            keyHandler.HandlePageKey(e);
            if (e.Handled) return;
        }

        // 文字入力中の Esc で書きかけの内容ごと画面を離れないよう、入力欄からの Esc は戻る操作にしない
        if (e.Key == VirtualKey.Escape && e.OriginalSource is not (TextBox or PasswordBox or AutoSuggestBox))
        {
            RequestBack();
            e.Handled = true;
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var cur = element; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, ancestor)) return true;
        }
        return false;
    }

    private void OnBackAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RequestBack();
        args.Handled = true;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed)
        {
            RequestBack();
            e.Handled = true;
        }
    }
}
