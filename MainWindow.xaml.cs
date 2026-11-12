using DTTS.Common;
using DTTS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DTTS;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "DTTS - 4-Player Daifugo Tabletop Suite";

        // ナビゲーションサービスにルートFrameを設定
        var nav = App.Services.GetRequiredService<INavigationService>();
        nav.RootFrame = ContentFrame;

        // タイトル画面へ遷移
        nav.NavigateToTitle();
    }
}
