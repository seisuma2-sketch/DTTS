using System;
using System.Threading.Tasks;
using DTTS.Common;
using DTTS.Services.Settings;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;

namespace DTTS.Views;

public sealed partial class RuleSettingsPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public RuleSettingsViewModel ViewModel { get; }

    /// <summary>カテゴリの有効数バッジの背景（1つでも有効ならテーマカラー）。</summary>
    public static Microsoft.UI.Xaml.Media.Brush BadgeBackground(bool hasEnabled)
        => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[hasEnabled ? "AppAccentSubtleBrush" : "AppBackgroundDeepBrush"];

    /// <summary>カテゴリの有効数バッジの文字色。</summary>
    public static Microsoft.UI.Xaml.Media.Brush BadgeForeground(bool hasEnabled)
        => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[hasEnabled ? "AppAccentBrush" : "AppTextMutedBrush"];

    /// <summary>カテゴリの開閉の矢印（開いているときは上向き）。</summary>
    public static string ChevronGlyph(bool expanded) => expanded ? "" : "";

    // ------------------------------------------------------------------
    // カテゴリの折りたたみ（Expander の代わり。高さのアニメーションをせず、即座に開いて不透明度だけフェードする）
    // ------------------------------------------------------------------

    private static readonly TimeSpan CategoryFadeDuration = TimeSpan.FromMilliseconds(140);

    private void OnCategoryHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleCategoryViewModel category })
        {
            FrameProbe.Begin(category.IsExpanded ? "collapse " + category.Title : "expand " + category.Title);
            category.IsExpanded = !category.IsExpanded;
        }
    }

    /// <summary>
    /// カテゴリの中身に、表示されたときの軽いフェードイン（不透明度 0 → 1）を付ける。
    /// コンポジションの暗黙アニメーションなので、レイアウト（計測・配置）は開いた瞬間の1回だけで済む。
    /// </summary>
    private void OnCategoryContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        QueueWarmUp(element);
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = CategoryFadeDuration;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(element, fade);
        }
        catch (Exception ex)
        {
            // 演出が付けられなくても、開閉そのものは動く
            App.WriteCrashLog("RuleSettings.CategoryFade", ex);
        }
    }

    // 初めて開くときは中のスイッチ・選択欄を作る分（1カテゴリ 50〜150ms）だけ引っかかるため、
    // 画面を開いた後の空き時間に、閉じたままのカテゴリを1つずつ裏で計測しておく（作った部品はそのまま使われる）
    private readonly System.Collections.Generic.Queue<UIElement> _warmUpQueue = new();
    private bool _warmUpScheduled;

    private void QueueWarmUp(UIElement element)
    {
        _warmUpQueue.Enqueue(element);
        ScheduleWarmUp();
    }

    private void ScheduleWarmUp()
    {
        if (_warmUpScheduled || _warmUpQueue.Count == 0) return;
        _warmUpScheduled = true;
        // 入力や描画より後回しの優先度で、1回に1カテゴリだけ処理する（操作の邪魔をしない）
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WarmUpNext);
    }

    private void WarmUpNext()
    {
        _warmUpScheduled = false;
        if (_warmUpQueue.Count == 0) return;
        var element = _warmUpQueue.Dequeue();
        try
        {
            // 閉じたまま画面にあるものだけ。描画の前に閉じ直すので、見た目には何も起きない
            if (element.Visibility == Visibility.Collapsed && element.XamlRoot is not null)
            {
                element.Visibility = Visibility.Visible;
                element.UpdateLayout();
                element.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("RuleSettings.WarmUp", ex);
        }
        ScheduleWarmUp();
    }

    /// <summary>
    /// 開閉時のフレーム間隔の計測（起動引数 --frame-probe のときだけ）。開閉から 600ms の間の
    /// フレーム間隔（最大・平均・33ms を超えた回数）を %TEMP%\DTTS_frameprobe.log に書く。
    /// </summary>
    private static class FrameProbe
    {
#if DEBUG
        private static readonly bool Enabled = Array.Exists(Environment.GetCommandLineArgs(),
            a => string.Equals(a, "--frame-probe", StringComparison.OrdinalIgnoreCase));
#else
        private static readonly bool Enabled = false;
#endif

        private static string _label = "";
        private static System.Diagnostics.Stopwatch? _clock;
        private static TimeSpan _last;
        private static readonly System.Collections.Generic.List<double> Intervals = new();

        public static void Begin(string label)
        {
            if (!Enabled) return;
            End();
            _label = label;
            Intervals.Clear();
            _clock = System.Diagnostics.Stopwatch.StartNew();
            _last = TimeSpan.Zero;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }

        private static void OnRendering(object? sender, object e)
        {
            if (_clock is null) return;
            var now = _clock.Elapsed;
            Intervals.Add((now - _last).TotalMilliseconds);
            _last = now;
            if (now > TimeSpan.FromMilliseconds(600)) End();
        }

        private static void End()
        {
            if (_clock is null) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _clock = null;
            if (Intervals.Count == 0) return;
            double max = 0, sum = 0;
            int slow = 0;
            foreach (double v in Intervals)
            {
                max = Math.Max(max, v);
                sum += v;
                if (v > 33.4) slow++;
            }
            string line = $"{DateTime.Now:HH:mm:ss.fff}\t{_label}\tframes={Intervals.Count}\tfirst={Intervals[0]:F1}ms\tmax={max:F1}ms\tavg={sum / Intervals.Count:F1}ms\tover33ms={slow}";
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DTTS_frameprobe.log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // 計測ログが書けなくても画面の動作には影響させない
            }
        }
    }

    public RuleSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<RuleSettingsViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("RuleSettings.OnPageLoaded", ex);
            ViewModel.StatusMessage = $"設定の読み込みに失敗しました: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // ルール詳細ガイド：左の項目にマウスを重ねる・クリック・フォーカスで右ペインに解説を出す
    // ------------------------------------------------------------------

    /// <summary>
    /// マウスを重ねてからガイドを切り替えるまでの時間。右ペインへマウスを動かす途中で
    /// 別の行を横切っても、読んでいる解説が切り替わらないようにする。
    /// </summary>
    private static readonly TimeSpan GuideHoverDelay = TimeSpan.FromMilliseconds(180);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _guideHoverTimer;
    private RuleGuide? _hoverCandidate;

    /// <summary>行（テンプレートのルート要素）から、その項目を取り出す。Tag に入れた項目を優先し、なければ DataContext を見る。</summary>
    private static T? ItemOf<T>(object sender) where T : class
        => sender is FrameworkElement element ? (element.Tag as T ?? element.DataContext as T) : null;

    private static RuleGuide? GuideOf(object sender) => ItemOf<IRuleGuideSource>(sender)?.Guide;

    private void OnRuleRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoverCandidate = GuideOf(sender);
        if (_hoverCandidate is null) return;
        if (_guideHoverTimer is null)
        {
            _guideHoverTimer = DispatcherQueue.CreateTimer();
            _guideHoverTimer.IsRepeating = false;
            _guideHoverTimer.Interval = GuideHoverDelay;
            _guideHoverTimer.Tick += (_, _) => ViewModel.ShowGuide(_hoverCandidate);
        }
        _guideHoverTimer.Stop();
        _guideHoverTimer.Start();
    }

    private void OnRuleRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // すぐ通り過ぎた行は表示しない
        if (ReferenceEquals(_hoverCandidate, GuideOf(sender)))
        {
            _guideHoverTimer?.Stop();
            _hoverCandidate = null;
        }
    }

    /// <summary>クリック（タップ）した行は待たずに表示する。</summary>
    private void OnRuleRowTapped(object sender, TappedRoutedEventArgs e) => ShowGuideNow(sender);

    /// <summary>キーボードのフォーカスが入った行（スイッチ・選択肢）も表示する。</summary>
    private void OnRuleRowGotFocus(object sender, RoutedEventArgs e) => ShowGuideNow(sender);

    private void ShowGuideNow(object sender)
    {
        _guideHoverTimer?.Stop();
        ViewModel.ShowGuide(GuideOf(sender));
    }

    // ------------------------------------------------------------------
    // マイプリセット
    // ------------------------------------------------------------------

    private void OnPresetNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.SaveCurrentAsPresetCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLoadPresetClicked(object sender, RoutedEventArgs e)
    {
        if (ItemOf<CustomPresetItemViewModel>(sender) is { } item)
        {
            ViewModel.LoadCustomPresetCommand.Execute(item);
        }
    }

    private bool _isPresetDialogOpen;

    /// <summary>削除は元に戻せないので、確認してから行う。</summary>
    private async void OnDeletePresetClicked(object sender, RoutedEventArgs e)
    {
        if (_isPresetDialogOpen || ItemOf<CustomPresetItemViewModel>(sender) is not { } item) return;
        _isPresetDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "マイプリセットの削除",
                Content = $"「{item.Name}」を削除しますか？（現在編集中のルールには影響しません）",
                PrimaryButtonText = "削除する",
                CloseButtonText = "やめる",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteCustomPresetCommand.ExecuteAsync(item);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("RuleSettings.DeletePreset", ex);
        }
        finally
        {
            _isPresetDialogOpen = false;
        }
    }

    private void OnThemeButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ThemeColor color)
        {
            ViewModel.SelectThemeCommand.Execute(color);
        }
    }

    private void OnSelectDefaultSkinClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectSkinCommand.Execute(null);
    }

    private async void OnImportZipClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".zip");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.ImportSkinZipCommand.ExecuteAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("RuleSettings.ImportZip", ex);
            ViewModel.StatusMessage = $"スキンの読み込みに失敗しました: {ex.Message}";
        }
    }
}
