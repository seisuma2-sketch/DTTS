using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using DTTS.Core.Ai;
using DTTS.Core.Models;

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

/// <summary>革命時の卓背景カラー。</summary>
public enum RevolutionBgColor : byte
{
    /// <summary>深紅（クリムゾン）</summary>
    DeepCrimson = 0,
    /// <summary>バイオレット</summary>
    VioletNight,
    /// <summary>アンバーゴールド</summary>
    AmberFlame,
    /// <summary>ディープエメラルド</summary>
    EmeraldAbyss,
    /// <summary>変更しない（通常の卓背景のまま）</summary>
    None,
}

/// <summary>革命時背景カラーの表示情報とグラデーション色。</summary>
public sealed record RevolutionBgColorInfo(
    RevolutionBgColor Color,
    string DisplayName,
    string CenterHex,
    string MidHex,
    string OuterHex,
    string EdgeHex)
{
    public static IReadOnlyList<RevolutionBgColorInfo> All { get; } = new[]
    {
        new RevolutionBgColorInfo(RevolutionBgColor.DeepCrimson, "深紅（クリムゾン）", "#FF461926", "#FF34131E", "#FF240D15", "#FF14070C"),
        new RevolutionBgColorInfo(RevolutionBgColor.VioletNight, "バイオレット", "#FF381E52", "#FF2B1640", "#FF1E102E", "#FF11081B"),
        new RevolutionBgColorInfo(RevolutionBgColor.AmberFlame, "アンバーゴールド", "#FF472C14", "#FF36210F", "#FF26170A", "#FF140C05"),
        new RevolutionBgColorInfo(RevolutionBgColor.EmeraldAbyss, "ディープエメラルド", "#FF143E32", "#FF0F3027", "#FF0A221B", "#FF05130F"),
        new RevolutionBgColorInfo(RevolutionBgColor.None, "変更しない（通常）", "#FF223047", "#FF1D2636", "#FF151B26", "#FF0E121A"),
    };

    public static RevolutionBgColorInfo Get(RevolutionBgColor color)
        => All.FirstOrDefault(c => c.Color == color) ?? All[0];
}

/// <summary>プレイヤーが選択できるアイコンの定義。</summary>
public sealed record PlayerIconInfo(string Id, string DisplayName, string Glyph, string AccentHex)
{
    public static IReadOnlyList<PlayerIconInfo> All { get; } = new[]
    {
        new PlayerIconInfo("default", "標準", "\uE77B", "#3E8EF7"),
        new PlayerIconInfo("crown", "王冠", "\uE734", "#F4C430"),
        new PlayerIconInfo("flame", "ボム・炎", "\uE945", "#E5484D"),
        new PlayerIconInfo("bolt", "稲妻", "\uE945", "#FF8A3D"),
        new PlayerIconInfo("heart", "ハート", "\uEB52", "#FF4FA3"),
        new PlayerIconInfo("shield", "シールド", "\uEA18", "#2FBF84"),
        new PlayerIconInfo("spark", "スター", "\uE735", "#9B6CFF"),
        new PlayerIconInfo("game", "ゲーム", "\uE7FC", "#5EC8FF"),
        new PlayerIconInfo("music", "ミュージック", "\uEC4F", "#B6F24A"),
        new PlayerIconInfo("bot", "メカ", "\uE99A", "#C4CBD8"),
    };

    public static PlayerIconInfo Get(string? id)
        => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal)) ?? All[0];
}

/// <summary>操作ガイド・割り込み確認の表示レベル。</summary>
public enum GuideLevel : byte
{
    /// <summary>初心者用：ルールや操作のヒントを出す。</summary>
    Beginner = 0,
    /// <summary>中級者用（標準）：革命や特殊効果の初回など、重要な場面だけ控えめなヒント。</summary>
    Intermediate,
    /// <summary>上級者用：ヒント・手番の案内・まとめて選ぶ候補を出さない。</summary>
    Expert,
}

/// <summary>操作ガイドのレベルの表示名と補助判定。</summary>
public static class GuideLevelInfo
{
    /// <summary>設定画面に並べる順。</summary>
    public static IReadOnlyList<GuideLevel> All { get; } = new[] { GuideLevel.Beginner, GuideLevel.Intermediate, GuideLevel.Expert };

    /// <summary>表示名。</summary>
    public static string DisplayName(this GuideLevel level) => level switch
    {
        GuideLevel.Beginner => "初心者用（チュートリアルあり）",
        GuideLevel.Expert => "上級者用（ガイドなし）",
        _ => "中級者用（標準・ときどきヒント）",
    };

    /// <summary>説明文。</summary>
    public static string Description(this GuideLevel level) => level switch
    {
        GuideLevel.Beginner => "ルールや操作のヒントを、場面ごとに表示します。",
        GuideLevel.Expert => "ヒント・手番の案内（「あなたのターンです」など）・「まとめて選ぶ」の候補を表示しない、すっきりした画面です。",
        _ => "革命や特殊効果の初回など、重要な場面でだけ控えめなヒントを表示します。",
    };

    /// <summary>
    /// 止められる手（4止め・砂嵐）を持っていなくても、8切り・ろくろ首・99車・エンペラーの割り込みの確認を出すか。
    /// 人が操作する席には、ガイドのレベルに関わらず常に確認を出す（勝手に場が流れないように）。
    /// </summary>
    public static bool AlwaysAsksInterrupt(this GuideLevel level) => true;
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

    /// <summary>手札カードのホバー演出を有効にする。</summary>
    public bool CardHoverEnabled { get; set; } = true;

    /// <summary>効果音を有効にする。</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>効果音の音量（0〜100）。</summary>
    public int SeVolume { get; set; } = DefaultSeVolume;

    /// <summary>BGM の音量（0〜100）。BGM は未実装で、将来の追加に備えて値だけ保持する。</summary>
    public int BgmVolume { get; set; } = DefaultBgmVolume;

    /// <summary>ゲームの進行スピード（NPCが考える時間）。設定画面と対戦画面のどちらからでも変えられる。</summary>
    public GameSpeed GameSpeed { get; set; } = GameSpeed.Normal;

    /// <summary>操作ガイド・割り込み確認の表示レベル（既定は中級者用：止められる手を持つときだけ確認）。</summary>
    public GuideLevel GuideLevel { get; set; } = GuideLevel.Intermediate;

    /// <summary>出せる手がないとき（通常ターン・割り込みとも）に自動でパスするか。対戦中も切り替え可能。</summary>
    public bool AutoPassWhenCannotPlay { get; set; } = false;

    /// <summary>革命中の卓背景カラー。</summary>
    public RevolutionBgColor RevolutionBgColor { get; set; } = RevolutionBgColor.DeepCrimson;

    /// <summary>プレイヤーのアイコンID（プリセット識別子）。</summary>
    public string PlayerIconId { get; set; } = "default";

    /// <summary>カスタム画像アバターのファイルパス（任意）。</summary>
    public string? CustomAvatarPath { get; set; }

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
        if (!Enum.IsDefined(RevolutionBgColor))
        {
            RevolutionBgColor = RevolutionBgColor.DeepCrimson;
        }
        if (!Enum.IsDefined(GameSpeed))
        {
            GameSpeed = GameSpeed.Normal;
        }
        if (!Enum.IsDefined(GuideLevel))
        {
            GuideLevel = GuideLevel.Intermediate;
        }
        PlayerIconId = PlayerIconInfo.Get(PlayerIconId).Id;
        CustomAvatarPath = string.IsNullOrWhiteSpace(CustomAvatarPath) ? null : CustomAvatarPath.Trim();
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

/// <summary>ユーザーが名前を付けて保存したルール構成（マイプリセット）1件。</summary>
public sealed class CustomRulePreset
{
    /// <summary>プリセット名（一覧内で一意）。</summary>
    public string Name { get; set; } = "";

    /// <summary>ルール設定（<see cref="RuleSettings.ToJson"/> の出力）。</summary>
    public string RulesJson { get; set; } = "";

    /// <summary>保存したテーマカラー（任意）。</summary>
    public ThemeColor? ThemeColor { get; set; }

    /// <summary>保存した革命時背景カラー（任意）。</summary>
    public RevolutionBgColor? RevolutionBgColor { get; set; }

    /// <summary>保存した日時。</summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>保存されているルール設定を復元する（壊れていれば null）。</summary>
    public RuleSettings? ToRules()
    {
        if (string.IsNullOrWhiteSpace(RulesJson))
        {
            return null;
        }
        try
        {
            return RuleSettings.FromJson(RulesJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>マイプリセットの一覧（保存の新しい順）。</summary>
public sealed class CustomRulePresetList
{
    /// <summary>保存できる件数の上限。</summary>
    public const int MaxCount = 30;

    /// <summary>プリセット名の最大文字数。</summary>
    public const int MaxNameLength = 24;

    /// <summary>プリセット（保存の新しい順）。</summary>
    public List<CustomRulePreset> Presets { get; set; } = new();

    /// <summary>名前を整える（前後の空白・制御文字を除き、長すぎる分は切る）。使えない名前なら null。</summary>
    public static string? SanitizeName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength].Trim() : cleaned;
    }

    /// <summary>
    /// 現在のルール構成を名前を付けて保存する。同じ名前があれば上書きし、先頭（最新）へ移す。
    /// </summary>
    /// <returns>保存したプリセット。名前が使えない、または上限に達していて新規追加できない場合は null。</returns>
    public CustomRulePreset? Upsert(
        string? name,
        RuleSettings rules,
        DateTimeOffset now,
        ThemeColor? themeColor = null,
        RevolutionBgColor? revolutionBgColor = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var clean = SanitizeName(name);
        if (clean is null)
        {
            return null;
        }
        var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, clean, StringComparison.Ordinal));
        if (existing is null && Presets.Count >= MaxCount)
        {
            return null;
        }
        if (existing is not null)
        {
            Presets.Remove(existing);
        }
        var preset = new CustomRulePreset
        {
            Name = clean,
            RulesJson = rules.ToJson(),
            ThemeColor = themeColor,
            RevolutionBgColor = revolutionBgColor,
            SavedAt = now,
        };
        Presets.Insert(0, preset);
        return preset;
    }

    /// <summary>名前を指定して削除する。削除したら true。</summary>
    public bool Remove(string? name)
        => Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal)) > 0;

    /// <summary>壊れた項目・重複した名前を取り除き、上限件数に収める（破損データ対策）。</summary>
    public void Normalize()
    {
        Presets ??= new List<CustomRulePreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<CustomRulePreset>();
        foreach (var p in Presets)
        {
            if (p is null)
            {
                continue;
            }
            var clean = SanitizeName(p.Name);
            if (clean is null || p.ToRules() is null || !seen.Add(clean))
            {
                continue;
            }
            p.Name = clean;
            valid.Add(p);
            if (valid.Count >= MaxCount)
            {
                break;
            }
        }
        Presets = valid;
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
[JsonSerializable(typeof(CustomRulePresetList))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
