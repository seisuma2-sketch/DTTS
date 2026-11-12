using System;
using System.IO;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Services.Database;
using DTTS.Services.Feedback;
using DTTS.Services.Skin;
using DTTS.ViewModels;
using DTTS.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace DTTS;

public partial class App : Application
{
    private Window? _window;
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 1. データフォルダのパス決定
        string appDataPath;
        try
        {
            appDataPath = ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DTTS");
        }
        Directory.CreateDirectory(appDataPath);

        // 2. DIサービスコレクションの構築
        var services = new ServiceCollection();

        // データベース & リポジトリ
        string dbPath = Path.Combine(appDataPath, AppDatabaseContext.DefaultFileName);
        var db = new AppDatabaseContext(dbPath);
        services.AddSingleton(db);
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<MatchHistoryRepository>();
        services.AddSingleton<FeedbackRepository>();

        // スキンマネージャー
        string skinsDir = Path.Combine(appDataPath, "skins");
        Directory.CreateDirectory(skinsDir);
        var skinManager = new CardSkinManager(skinsDir);
        services.AddSingleton<ICardSkinManager>(skinManager);
        CardControl.InitializeSkinManager(skinManager);

        // ネットワーク & 接続モニター
        services.AddSingleton<IConnectivityMonitor, WindowsConnectivityMonitor>();

        // ナビゲーション
        services.AddSingleton<INavigationService, NavigationService>();

        // ViewModels
        services.AddTransient<TitleViewModel>();
        services.AddTransient<GameTableViewModel>();

        Services = services.BuildServiceProvider();

        // 3. データベース初期化
        await db.InitializeAsync();

        // 4. 初回起動時の端末ID生成と設定ロード
        var settingsRepo = Services.GetRequiredService<SettingsRepository>();
        var clientId = await settingsRepo.GetAsync("device_id");
        if (string.IsNullOrEmpty(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
            await settingsRepo.SetAsync("device_id", clientId);
        }

        // 5. 外見設定（テーマカラー・スキン）の適用
        var appearance = await settingsRepo.LoadAppearanceAsync();
        ThemeManager.ApplyTheme(appearance.ThemeColor);
        if (!string.IsNullOrEmpty(appearance.SkinPackId))
        {
            skinManager.SetActivePack(appearance.SkinPackId);
        }

        // 6. ウィンドウ表示
        _window = new MainWindow();
        _window.Activate();
    }
}
