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
    private static ServiceProvider? _serviceProvider;
    public static Window MainWindowInstance { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>アプリ終了時に DI コンテナと登録済みの IDisposable サービス（AudioGraph・FeedbackService 等）を破棄する。</summary>
    internal static void ShutdownServices()
    {
        var sp = System.Threading.Interlocked.Exchange(ref _serviceProvider, null);
        if (sp is null) return;
        try
        {
            sp.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrashLog("App.ShutdownServices", ex);
        }
    }

    /// <summary>パッケージまたはアセンブリから現在のアプリバージョンを取得する。</summary>
    internal static string GetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.1.7";
        }
    }

    public App()
    {
        InitializeComponent();

        // 未処理例外をアプリのデータフォルダの crash.log に残す（起動直後のクラッシュの原因調査用）
        UnhandledException += (s, e) => WriteCrashLog("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteCrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) => WriteCrashLog("UnobservedTask", e.Exception);    }

    private static readonly object CrashLogLock = new();
    private const long MaxCrashLogBytes = 1024 * 1024; // 1 MiB

    /// <summary>例外の内容を crash.log に追記する（排他制御・1 MiB ローテーション・ユーザーパスマスク付き）。</summary>
    internal static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            string folder;
            try
            {
#if DEBUG
                folder = AppLaunchOptions.DataDirectoryForCurrentProcess() is { } dir
                    ? Path.GetFullPath(dir)
                    : ApplicationData.Current.LocalFolder.Path;
#else
                folder = ApplicationData.Current.LocalFolder.Path;
#endif
            }
            catch
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DTTS");
            }
            Directory.CreateDirectory(folder);
            string logPath = Path.Combine(folder, "crash.log");
            string text = exception?.ToString() ?? "(null)";
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }
            lock (CrashLogLock)
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxCrashLogBytes)
                {
                    string rotated = logPath + ".1";
                    File.Move(logPath, rotated, overwrite: true);
                }
                File.AppendAllText(
                    logPath,
                    $"[{DateTimeOffset.Now:O}] {source}: {text}{Environment.NewLine}{Environment.NewLine}");
            }
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
#if DEBUG
        // 検証用：--data-dir で保存先を分ける（同じ PC でホストとゲストを2つ起動するときなど）
        if (AppLaunchOptions.DataDirectoryForCurrentProcess() is { } overrideDir)
        {
            appDataPath = Path.GetFullPath(overrideDir);
        }
#endif
        Directory.CreateDirectory(appDataPath);

        // 2. データベース初期化と端末IDの先行確定（DI 解決時の同期ブロックと ID 不一致を防ぐ）
        string dbPath = Path.Combine(appDataPath, AppDatabaseContext.DefaultFileName);
        var db = new AppDatabaseContext(dbPath);
        await db.InitializeAsync();

        var settingsRepo = new SettingsRepository(db, new WindowsSettingsSecretProtector());
        var clientId = await settingsRepo.GetAsync("device_id");
        if (string.IsNullOrEmpty(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
            await settingsRepo.SetAsync("device_id", clientId);
        }

        // 3. DIサービスコレクションの構築
        var services = new ServiceCollection();

        // データベース & リポジトリ
        services.AddSingleton(db);
        services.AddSingleton(settingsRepo);
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
            return new BleNetworkService(dispatcher, clientId);
        });

        // フィードバックサービス
        services.AddSingleton(sp =>
        {
            var repo = sp.GetRequiredService<FeedbackRepository>();
            var settings = sp.GetRequiredService<SettingsRepository>();
            var connectivity = sp.GetRequiredService<IConnectivityMonitor>();
            // リダイレクトによる HTTPS→HTTP ダウングレードや巨大応答によるメモリ消費を防ぐ
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 64 * 1024,
            };
            return new FeedbackService(
                repo,
                () => Task.Run(() => settings.LoadFeedbackOptionsAsync()).GetAwaiter().GetResult(),
                http,
                connectivity,
                GetAppVersion(),
                ownsHttpClient: true
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

        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        // 4. 外見設定（テーマカラー・スキン・ホバー）の適用
        var appearance = await settingsRepo.LoadAppearanceAsync();
        ThemeManager.ApplyTheme(appearance.ThemeColor);
        CardControl.GlobalHoverEnabled = appearance.CardHoverEnabled;
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
