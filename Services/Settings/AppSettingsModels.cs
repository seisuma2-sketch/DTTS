using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using DTTS.Core.Ai;

namespace DTTS.Services.Settings;

/// <summary>テーマカラー（8色）。</summary>
public enum ThemeColor : byte
{
    /// <summary>クリムゾンレッド</summary>
    CrimsonRed = 0,
    /// <summary>ディープアズール</summary>
    DeepAzure,
    /// <summary>ライムネオン</summary>
    LimeNeon,
    /// <summary>サンセットオレンジ</summary>
    SunsetOrange,
    /// <summary>ホットピンク</summary>
    HotPink,
    /// <summary>プラチナホワイト</summary>
    PlatinumWhite,
    /// <summary>エメラルドグリーン</summary>
    EmeraldGreen,
    /// <summary>バイオレットパープル</summary>
    VioletPurple,
}

/// <summary>テーマカラーの表示名と基調色（UI層でアクセント色リソースに展開する）。</summary>
/// <param name="Color">テーマカラー。</param>
/// <param name="DisplayName">表示名。</param>
/// <param name="AccentHex">アクセント色（#RRGGBB）。</param>
/// <param name="OnAccentHex">アクセント色の上に載せる文字色（#RRGGBB）。</param>
public sealed record ThemeColorInfo(ThemeColor Color, string DisplayName, string AccentHex, string OnAccentHex)
{
    /// <summary>全テーマカラーの定義（ソフトダーク背景上での視認性を考慮した中〜高明度の色）。</summary>
    public static IReadOnlyList<ThemeColorInfo> All { get; } = new[]
    {
        new ThemeColorInfo(ThemeColor.CrimsonRed, "クリムゾンレッド", "#E5484D", "#FFFFFF"),
        new ThemeColorInfo(ThemeColor.DeepAzure, "ディープアズール", "#3E8EF7", "#FFFFFF"),
        new ThemeColorInfo(ThemeColor.LimeNeon, "ライムネオン", "#B6F24A", "#1A2205"),
        new ThemeColorInfo(ThemeColor.SunsetOrange, "サンセットオレンジ", "#FF8A3D", "#2B1300"),
        new ThemeColorInfo(ThemeColor.HotPink, "ホットピンク", "#FF4FA3", "#FFFFFF"),
        new ThemeColorInfo(ThemeColor.PlatinumWhite, "プラチナホワイト", "#E8EAF0", "#1C1D22"),
        new ThemeColorInfo(ThemeColor.EmeraldGreen, "エメラルドグリーン", "#2FBF84", "#FFFFFF"),
        new ThemeColorInfo(ThemeColor.VioletPurple, "バイオレットパープル", "#9B6CFF", "#FFFFFF"),
    };

    /// <summary>テーマカラーの定義を取得する。</summary>
    public static ThemeColorInfo Get(ThemeColor color) => All.FirstOrDefault(t => t.Color == color) ?? All[1];
}

/// <summary>外見設定。</summary>
public sealed class AppearanceSettings
{
    /// <summary>テーマカラー。</summary>
    public ThemeColor ThemeColor { get; set; } = ThemeColor.DeepAzure;

    /// <summary>使用するスキンパックID（null ならベクター描画の標準カード）。</summary>
    public string? SkinPackId { get; set; }

    /// <summary>アニメーションを有効にする。</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>効果音を有効にする。</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>効果音の音量（0〜100）。</summary>
    public int SeVolume { get; set; } = DefaultSeVolume;

    /// <summary>BGM の音量（0〜100）。BGM は未実装で、将来の追加に備えて値だけ保持する。</summary>
    public int BgmVolume { get; set; } = DefaultBgmVolume;

    /// <summary>効果音の既定音量。</summary>
    public const int DefaultSeVolume = 80;

    /// <summary>BGM の既定音量。</summary>
    public const int DefaultBgmVolume = 60;

    /// <summary>範囲外の値を補正する（破損データ・旧バージョンの設定対策）。</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(ThemeColor))
        {
            ThemeColor = ThemeColor.DeepAzure;
        }
        SeVolume = Math.Clamp(SeVolume, 0, 100);
        BgmVolume = Math.Clamp(BgmVolume, 0, 100);
    }

    /// <summary>0〜100 の音量を 0.0〜1.0 に変換する。</summary>
    public static double ToUnitVolume(int volume) => Math.Clamp(volume, 0, 100) / 100.0;
}

/// <summary>NPC席の設定。</summary>
public sealed class NpcSeatSetting
{
    /// <summary>表示名。</summary>
    public string Name { get; set; } = "NPC";

    /// <summary>強さ。</summary>
    public NpcDifficulty Difficulty { get; set; } = NpcDifficulty.Normal;

    /// <summary>性格。</summary>
    public NpcPersonality Personality { get; set; } = NpcPersonality.Steady;

    /// <summary>NPCプロファイルに変換する。</summary>
    public NpcProfile ToProfile() => new(Difficulty, Personality);
}

/// <summary>対戦メンバー設定（プレイヤー名とNPC3席）。</summary>
public sealed class NpcSettings
{
    /// <summary>プレイヤー（この端末の人間）の名前。</summary>
    public string PlayerName { get; set; } = "あなた";

    /// <summary>NPC席（座席1〜3）。</summary>
    public List<NpcSeatSetting> Seats { get; set; } = new()
    {
        new NpcSeatSetting { Name = "アカネ", Difficulty = NpcDifficulty.Normal, Personality = NpcPersonality.Steady },
        new NpcSeatSetting { Name = "ソウタ", Difficulty = NpcDifficulty.Normal, Personality = NpcPersonality.Aggressive },
        new NpcSeatSetting { Name = "ミオ", Difficulty = NpcDifficulty.Normal, Personality = NpcPersonality.RevolutionSeeker },
    };

    /// <summary>不正値を補正する。</summary>
    public void Normalize()
    {
        PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "あなた" : PlayerName.Trim();
        Seats ??= new List<NpcSeatSetting>();
        var defaults = new NpcSettings().Seats;
        while (Seats.Count < 3)
        {
            Seats.Add(defaults[Seats.Count]);
        }
        foreach (var seat in Seats)
        {
            if (!Enum.IsDefined(seat.Difficulty))
            {
                seat.Difficulty = NpcDifficulty.Normal;
            }
            if (!Enum.IsDefined(seat.Personality))
            {
                seat.Personality = NpcPersonality.Steady;
            }
            seat.Name = string.IsNullOrWhiteSpace(seat.Name) ? "NPC" : seat.Name.Trim();
        }
    }
}

/// <summary>フィードバック送信設定。</summary>
public sealed class FeedbackOptions
{
    /// <summary>
    /// 送信先の Webhook URL（Google Apps Script の Web アプリ URL 等）。空なら端末内に蓄積するだけで送信しない。
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>オンライン時に自動でバックグラウンド送信する。</summary>
    public bool AutoSend { get; set; } = true;

    /// <summary>アプリのバージョン・OS 情報を添付する（個人を特定する情報は含めない）。</summary>
    public bool IncludeDiagnostics { get; set; } = true;
}

/// <summary>設定DTO用の System.Text.Json ソース生成コンテキスト（トリミング安全）。</summary>
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppearanceSettings))]
[JsonSerializable(typeof(NpcSettings))]
[JsonSerializable(typeof(FeedbackOptions))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
