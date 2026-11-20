using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Models;
using DTTS.Services.Settings;

namespace DTTS.Services.Database;

/// <summary>
/// 設定値に含まれる機密文字列（Webhook URL 等）を保存時に暗号化・読み込み時に復号する抽象化。
/// </summary>
public interface ISettingsSecretProtector
{
    string Protect(string plainText);
    string Unprotect(string storedText);
}

/// <summary>
/// キー・値形式の設定保存（ルール設定・外見・NPC設定・フィードバック設定）。
/// 値はJSONで保存し、破損・旧形式のデータは既定値へフォールバックする。
/// </summary>
public sealed class SettingsRepository
{
    /// <summary>ルール設定のキー。</summary>
    public const string RulesKey = "rules";
    /// <summary>外見設定のキー。</summary>
    public const string AppearanceKey = "appearance";
    /// <summary>NPC設定のキー。</summary>
    public const string NpcKey = "npc";
    /// <summary>フィードバック設定のキー。</summary>
    public const string FeedbackKey = "feedback";
    /// <summary>マイプリセット（名前を付けて保存したルール構成）のキー。</summary>
    public const string RulePresetsKey = "rule_presets";

    private readonly AppDatabaseContext _db;
    private readonly ISettingsSecretProtector? _secretProtector;

    // 外見設定の「読み込み → 一部変更 → 保存」を直列にする（サウンド切替と進行スピード切替が互いの変更を消さないように）
    private readonly SemaphoreSlim _appearanceUpdateLock = new(1, 1);

    /// <summary>リポジトリを生成する。</summary>
    public SettingsRepository(AppDatabaseContext db, ISettingsSecretProtector? secretProtector = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _secretProtector = secretProtector;
    }

    /// <summary>設定値を読み込む（なければ null）。</summary>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>設定値を保存する（上書き）。</summary>
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings(key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now", AppDatabaseContext.ToDbTime(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>設定値を削除する（次回読み込み時は既定値）。</summary>
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM app_settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 型付きの読み書き
    // ------------------------------------------------------------------

    /// <summary>ルール設定を読み込む（未保存・破損時は既定値）。</summary>
    public async Task<RuleSettings> LoadRulesAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(RulesKey, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return RuleSettings.CreateDefault();
        }
        try
        {
            return RuleSettings.FromJson(json);
        }
        catch (JsonException)
        {
            return RuleSettings.CreateDefault();
        }
    }

    /// <summary>ルール設定を保存する。</summary>
    public Task SaveRulesAsync(RuleSettings rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return SetAsync(RulesKey, rules.ToJson(), cancellationToken);
    }

    /// <summary>外見設定を読み込む。</summary>
    public async Task<AppearanceSettings> LoadAppearanceAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(AppearanceKey, cancellationToken).ConfigureAwait(false);
        var value = Deserialize(json, SettingsJsonContext.Default.AppearanceSettings) ?? new AppearanceSettings();
        value.Normalize();
        return value;
    }

    /// <summary>外見設定を保存する。</summary>
    public Task SaveAppearanceAsync(AppearanceSettings settings, CancellationToken cancellationToken = default)
        => SetAsync(AppearanceKey, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppearanceSettings), cancellationToken);

    /// <summary>
    /// 外見設定の一部だけを書き換えて保存する（対戦画面のサウンド・進行スピードのワンクリック切替用）。
    /// 複数の切替が同時に走っても、互いの変更を上書きしない。
    /// </summary>
    /// <returns>保存後の設定。</returns>
    public async Task<AppearanceSettings> UpdateAppearanceAsync(Action<AppearanceSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _appearanceUpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadAppearanceAsync(cancellationToken).ConfigureAwait(false);
            change(settings);
            settings.Normalize();
            await SaveAppearanceAsync(settings, cancellationToken).ConfigureAwait(false);
            return settings;
        }
        finally
        {
            _appearanceUpdateLock.Release();
        }
    }

    /// <summary>マイプリセットの一覧を読み込む（未保存・破損時は空）。</summary>
    public async Task<CustomRulePresetList> LoadRulePresetsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(RulePresetsKey, cancellationToken).ConfigureAwait(false);
        var value = Deserialize(json, SettingsJsonContext.Default.CustomRulePresetList) ?? new CustomRulePresetList();
        value.Normalize();
        return value;
    }

    /// <summary>マイプリセットの一覧を保存する。</summary>
    public Task SaveRulePresetsAsync(CustomRulePresetList presets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presets);
        return SetAsync(RulePresetsKey, JsonSerializer.Serialize(presets, SettingsJsonContext.Default.CustomRulePresetList), cancellationToken);
    }

    /// <summary>NPC設定を読み込む。</summary>
    public async Task<NpcSettings> LoadNpcAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(NpcKey, cancellationToken).ConfigureAwait(false);
        var value = Deserialize(json, SettingsJsonContext.Default.NpcSettings) ?? new NpcSettings();
        value.Normalize();
        return value;
    }

    /// <summary>NPC設定を保存する。</summary>
    public Task SaveNpcAsync(NpcSettings settings, CancellationToken cancellationToken = default)
        => SetAsync(NpcKey, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.NpcSettings), cancellationToken);

    /// <summary>フィードバック設定を読み込む。</summary>
    public async Task<FeedbackOptions> LoadFeedbackOptionsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(FeedbackKey, cancellationToken).ConfigureAwait(false);
        var options = Deserialize(json, SettingsJsonContext.Default.FeedbackOptions) ?? new FeedbackOptions();
        if (!string.IsNullOrEmpty(options.WebhookUrl) && _secretProtector is not null)
        {
            options.WebhookUrl = _secretProtector.Unprotect(options.WebhookUrl);
        }
        return options;
    }

    /// <summary>フィードバック設定を保存する。</summary>
    public Task SaveFeedbackOptionsAsync(FeedbackOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var toStore = new FeedbackOptions
        {
            WebhookUrl = !string.IsNullOrEmpty(options.WebhookUrl) && _secretProtector is not null
                ? _secretProtector.Protect(options.WebhookUrl)
                : options.WebhookUrl,
            AutoSend = options.AutoSend,
            IncludeDiagnostics = options.IncludeDiagnostics,
        };
        return SetAsync(FeedbackKey, JsonSerializer.Serialize(toStore, SettingsJsonContext.Default.FeedbackOptions), cancellationToken);
    }

    private static T? Deserialize<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null; // 破損データは既定値で置き換える
        }
    }
}
