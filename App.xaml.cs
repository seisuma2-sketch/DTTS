using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Services;
using DTTS.Services.Audio;
using DTTS.Services.Database;
using DTTS.Services.Feedback;
using DTTS.Services.Network;
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
    public static Window MainWindowInstance { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        // 未処理例外をアプリのデータフォルダの crash.log に残す（起動直後のクラッシュの原因調査用）
        UnhandledException += (s, e) => WriteCrashLog("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteCrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) => WriteCrashLog("UnobservedTask", e.Exception);    }

    /// <summary>例外の内容を crash.log に追記する（ログ出力自体の失敗は無視する）。</summary>
    internal static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            string folder;
            try
            {
                folder = AppLaunchOptions.DataDirectoryForCurrentProcess() is { } dir
                    ? Path.GetFullPath(dir)
                    : ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DTTS");
            }
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 起動処理の例外は未処理例外ハンドラを経由せずにプロセスが終了するため、ここで記録してから再送出する
        try
        {
            await LaunchAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnLaunched", ex);
            throw;
        }
    }

    private async Task LaunchAsync()
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
        // 検証用：--data-dir で保存先を分ける（同じ PC でホストとゲストを2つ起動するときなど）
        if (AppLaunchOptions.DataDirectoryForCurrentProcess() is { } overrideDir)
        {
            appDataPath = Path.GetFullPath(overrideDir);
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
        services.AddSingleton<INetworkService>(sp =>
        {
            var dispatcher = new DispatcherQueueSerialDispatcher(DispatcherQueue.GetForCurrentThread());
            var settingsRepo = sp.GetRequiredService<SettingsRepository>();
            var clientId = settingsRepo.GetAsync("device_id").GetAwaiter().GetResult() ?? Guid.NewGuid().ToString("N");
            return new LanNetworkService(dispatcher, clientId);
        });

        // フィードバックサービス
        services.AddSingleton(sp =>
        {
            var repo = sp.GetRequiredService<FeedbackRepository>();
            var settings = sp.GetRequiredService<SettingsRepository>();
            var connectivity = sp.GetRequiredService<IConnectivityMonitor>();
            var http = new HttpClient();
            return new FeedbackService(
                repo,
                () => settings.LoadFeedbackOptionsAsync().GetAwaiter().GetResult(),
                http,
                connectivity,
                "1.0.0"
            );
        });

        // 効果音（AudioGraph）
        var audio = AudioGraphService.CreateDefault();
        services.AddSingleton<IAudioService>(audio);

        // ナビゲーション
        services.AddSingleton<INavigationService, NavigationService>();

        // ViewModels
        services.AddTransient<TitleViewModel>();
        services.AddTransient<LobbyViewModel>();
        services.AddTransient<GameTableViewModel>();
        services.AddTransient<RuleSettingsViewModel>();
        services.AddTransient<ResultViewModel>();
        services.AddTransient<HelpViewModel>();
        services.AddTransient<FeedbackViewModel>();

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

        // 効果音：設定を反映し、読み込みはバックグラウンドで行う（起動を待たせない）
        audio.ApplySettings(appearance);
        if (AudioLaunchOptions.IsMuteRequestedForCurrentProcess())
        {
            // 自動テスト用：--mute / DTTS_MUTE=1 のときはこの回だけ無音にする（保存済みの設定は変えない）
            audio.SetSoundEnabled(false);
        }
        _ = audio.InitializeAsync();

        // 6. ウィンドウ表示
        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
    }
}
