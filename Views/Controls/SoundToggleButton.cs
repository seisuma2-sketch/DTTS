using System;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Services.Audio;
using DTTS.Services.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views.Controls;

/// <summary>
/// サウンドの ON/OFF をワンクリックで切り替えるボタン（タイトル画面・対戦画面のヘッダー用）。
/// 切り替えはすぐ音に反映し、外観設定（SoundEnabled）として保存する。
/// </summary>
public sealed partial class SoundToggleButton : Button
{
    private const string GlyphOn = "";  // Volume
    private const string GlyphOff = ""; // Mute

    // 連打しても保存が前後しないよう、保存は1つずつ行う
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    private readonly FontIcon _icon = new() { FontSize = 16 };

    public SoundToggleButton()
    {
        Content = _icon;
        Width = 36;
        Height = 32;
        Padding = new Thickness(0);
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        Click += OnClick;
        Loaded += (_, _) => UpdateLook();
    }

    private static IAudioService Audio => App.Services.GetRequiredService<IAudioService>();

    private void UpdateLook()
    {
        bool on = Audio.IsEnabled;
        _icon.Glyph = on ? GlyphOn : GlyphOff;
        string label = on ? "サウンド: オン（クリックでオフ）" : "サウンド: オフ（クリックでオン）";
        ToolTipService.SetToolTip(this, label);
        AutomationProperties.SetName(this, label);
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        bool enabled = !Audio.IsEnabled;
        Audio.SetSoundEnabled(enabled);
        UpdateLook();
        await SaveAsync(enabled);
    }

    private static async Task SaveAsync(bool enabled)
    {
        await SaveLock.WaitAsync();
        try
        {
            var repo = App.Services.GetRequiredService<SettingsRepository>();
            var appearance = await repo.LoadAppearanceAsync();
            appearance.SoundEnabled = enabled;
            await repo.SaveAppearanceAsync(appearance);
        }
        catch (Exception ex)
        {
            // 保存に失敗しても音の切り替え自体は効いている。次回起動時は以前の設定に戻る
            App.WriteCrashLog("SoundToggleButton.Save", ex);
        }
        finally
        {
            SaveLock.Release();
        }
    }
}
