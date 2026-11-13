using System;
using System.IO;
using DTTS.Common;
using DTTS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DTTS;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "DTTS - 4-Player Daifugo Tabletop Suite";

        // ウィンドウ左上・タスクバーのアイコン（パッケージ内の Assets\WindowIcon.ico）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WindowIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // ナビゲーションサービスにルートFrameを設定
        var nav = App.Services.GetRequiredService<INavigationService>();
        nav.RootFrame = ContentFrame;

        // マウスの「戻る」ボタン（子要素が処理済みのクリックでも拾う）
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), handledEventsToo: true);

        // タイトル画面へ遷移
        nav.NavigateToTitle();
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
