using System;
using System.Threading.Tasks;
using DTTS.Services.Audio;
using DTTS.Services.Database;
using DTTS.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DTTS.Views.Controls;

/// <summary>
/// タイトル画面・対戦画面のヘッダーにある音量ボタン。クリックすると小さな音量ポップアップ（Flyout）が開き、
/// 効果音のオン/オフと効果音の音量（0〜100）をその場で変えられる。
/// 変更はすぐ音に反映し、外観設定（SoundEnabled / SeVolume）として保存する（スライダーは少し待ってからまとめて保存）。
/// </summary>
public sealed partial class SoundToggleButton : Button
{
    private const string GlyphOn = "";   // Volume
    private const string GlyphLow = "";  // Volume1
    private const string GlyphOff = "";  // Mute

    /// <summary>スライダーを動かし終えてから保存するまでの待ち時間（ドラッグ中に何度も書き込まない）。</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(450);

    private readonly FontIcon _icon = new() { FontSize = 16 };
    private readonly ToggleSwitch _toggle;
    private readonly Slider _slider;
    private readonly TextBlock _valueText;
    private readonly Flyout _flyout;
    private DispatcherQueueTimer? _saveTimer;
    private bool _syncing;
    private bool _volumeDirty;

    public SoundToggleButton()
    {
        Content = _icon;
        Width = 36;
        Height = 32;
        Padding = new Thickness(0);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);

        _toggle = new ToggleSwitch
        {
            OnContent = "効果音 オン",
            OffContent = "効果音 オフ（ミュート）",
            MinWidth = 0,
        };
        AutomationProperties.SetName(_toggle, "効果音のオン・オフ");
        _toggle.Toggled += OnToggled;

        _slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            SmallChange = 5,
            LargeChange = 10,
            TickFrequency = 10,
            TickPlacement = TickPlacement.Outside,
            Width = 220,
            IsThumbToolTipEnabled = true,
        };
        AutomationProperties.SetName(_slider, "効果音の音量");
        _slider.ValueChanged += OnSliderValueChanged;
        _slider.PointerCaptureLost += (_, _) => PlayPreview(); // ドラッグを離したら今の音量で1回鳴らす

        _valueText = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32,
            TextAlignment = TextAlignment.Right,
        };

        var preview = new Button
        {
            Content = "試聴",
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        preview.Click += (_, _) => PlayPreview();

        var sliderRow = new Grid { ColumnSpacing = 10 };
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderRow.Children.Add(_slider);
        Grid.SetColumn(_valueText, 1);
        sliderRow.Children.Add(_valueText);

        var panel = new StackPanel { Spacing = 6, MinWidth = 270 };
        panel.Children.Add(new TextBlock { Text = "サウンド", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        panel.Children.Add(_toggle);
        panel.Children.Add(new TextBlock { Text = "効果音の音量", FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(sliderRow);
        panel.Children.Add(preview);
        panel.Children.Add(new TextBlock
        {
            Text = "変更はすぐに反映され、設定にも保存されます。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
        });

        _flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        _flyout.Opening += (_, _) => SyncFromAudio();
        _flyout.Closed += (_, _) => FlushSave();
        Flyout = _flyout;

        Loaded += (_, _) => UpdateLook();
        Unloaded += (_, _) => FlushSave();
    }

    private static IAudioService Audio => App.Services.GetRequiredService<IAudioService>();

    private static int CurrentVolume => (int)Math.Round(Math.Clamp(Audio.SeVolume, 0.0, 1.0) * 100);

    /// <summary>ポップアップを開くとき、今の音の設定を表示に反映する。</summary>
    private void SyncFromAudio()
    {
        _syncing = true;
        try
        {
            _toggle.IsOn = Audio.IsEnabled;
            _slider.Value = CurrentVolume;
            _slider.IsEnabled = Audio.IsEnabled;
            _valueText.Text = CurrentVolume.ToString();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateLook()
    {
        bool on = Audio.IsEnabled;
        int volume = CurrentVolume;
        _icon.Glyph = !on || volume == 0 ? GlyphOff : volume < 40 ? GlyphLow : GlyphOn;
        string label = on
            ? $"サウンド: オン・効果音 {volume}（クリックで音量を調節）"
            : "サウンド: オフ（クリックで音量を調節）";
        ToolTipService.SetToolTip(this, label);
        AutomationProperties.SetName(this, label);
    }

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool enabled = _toggle.IsOn;
        Audio.SetSoundEnabled(enabled);
        _slider.IsEnabled = enabled;
        UpdateLook();
        await SaveAsync(a => a.SoundEnabled = enabled, "SoundToggleButton.SaveEnabled");
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        int volume = (int)Math.Round(Math.Clamp(e.NewValue, 0, 100));
        Audio.SetSeVolume(AppearanceSettings.ToUnitVolume(volume)); // 音にはすぐ反映
        _valueText.Text = volume.ToString();
        UpdateLook();

        // 保存はドラッグが落ち着いてからまとめて行う
        _volumeDirty = true;
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherQueueTimer CreateSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = SaveDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FlushSave();
        return timer;
    }

    /// <summary>まだ保存していない音量があれば保存する（ポップアップを閉じたとき・画面を離れるときも）。</summary>
    private void FlushSave()
    {
        _saveTimer?.Stop();
        if (!_volumeDirty) return;
        _volumeDirty = false;
        int volume = CurrentVolume;
        _ = SaveAsync(a => a.SeVolume = volume, "SoundToggleButton.SaveVolume");
    }

    private static void PlayPreview()
    {
        if (Audio.IsEnabled)
        {
            Audio.Play(SoundEffect.CardPlay);
        }
    }

    private static async Task SaveAsync(Action<AppearanceSettings> update, string context)
    {
        try
        {
            // 連打しても、進行スピードの切り替えと重なっても、保存が前後・上書きしないよう直列に更新する
            var repo = App.Services.GetRequiredService<SettingsRepository>();
            await repo.UpdateAppearanceAsync(update);
        }
        catch (Exception ex)
        {
            // 保存に失敗しても音の変更自体は効いている。次回起動時は以前の設定に戻る
            App.WriteCrashLog(context, ex);
        }
    }
}
