using System;
using DTTS.Common;
using DTTS.Services.Settings;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
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
