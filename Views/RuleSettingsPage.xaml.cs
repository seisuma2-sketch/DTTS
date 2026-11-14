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

    public RuleSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<RuleSettingsViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
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
}
