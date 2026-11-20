using System;
using System.Globalization;
using DTTS.Services.Settings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DTTS.Common;

/// <summary>
/// アプリ全体のテーマカラー管理。8つのプリセットアクセントカラーを実行時に動的適用する。
/// </summary>
public static class ThemeManager
{
    public const string AccentColorKey = "AppAccentColor";
    public const string AccentBrushKey = "AppAccentBrush";
    public const string AccentGradientBrushKey = "AppAccentGradientBrush";
    public const string OnAccentBrushKey = "AppOnAccentBrush";
    public const string AccentSubtleBrushKey = "AppAccentSubtleBrush";

    public static ThemeColor CurrentThemeColor { get; private set; } = ThemeColor.DeepAzure;

    public static event EventHandler<ThemeColor>? ThemeChanged;

    public static void ApplyTheme(ThemeColor themeColor)
    {
        CurrentThemeColor = themeColor;
        var info = ThemeColorInfo.Get(themeColor);

        var accentColor = ParseHexColor(info.AccentHex);
        var onAccentColor = ParseHexColor(info.OnAccentHex);

        var appResources = Application.Current.Resources;

        // 既存のブラシインスタンスの色を直接書き換えることで、StaticResource 参照中の全画面に即時反映させる
        appResources[AccentColorKey] = accentColor;
        SetOrUpdateSolidBrush(appResources, AccentBrushKey, accentColor);
        SetOrUpdateSolidBrush(appResources, OnAccentBrushKey, onAccentColor);

        // 半透明アクセント（選択枠やハイライト用）
        var subtleColor = Color.FromArgb(48, accentColor.R, accentColor.G, accentColor.B);
        SetOrUpdateSolidBrush(appResources, AccentSubtleBrushKey, subtleColor);

        // グラデーションブラシ（Material 3 風）
        var lighterAccent = Color.FromArgb(
            255,
            (byte)Math.Min(255, accentColor.R + 40),
            (byte)Math.Min(255, accentColor.G + 40),
            (byte)Math.Min(255, accentColor.B + 40)
        );

        if (appResources.TryGetValue(AccentGradientBrushKey, out var existingGrad) && existingGrad is LinearGradientBrush grad && grad.GradientStops.Count >= 2)
        {
            grad.GradientStops[0].Color = lighterAccent;
            grad.GradientStops[1].Color = accentColor;
        }
        else
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1)
            };
            gradient.GradientStops.Add(new GradientStop { Color = lighterAccent, Offset = 0.0 });
            gradient.GradientStops.Add(new GradientStop { Color = accentColor, Offset = 1.0 });
            appResources[AccentGradientBrushKey] = gradient;
        }

        ThemeChanged?.Invoke(null, themeColor);
    }

    private static void SetOrUpdateSolidBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush scb)
        {
            scb.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    public static Color ParseHexColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.Transparent;
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(255, r, g, b);
        }
        if (hex.Length == 8)
        {
            byte a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(a, r, g, b);
        }
        return Colors.DodgerBlue;
    }
}

