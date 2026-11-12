using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Models;
using DTTS.Services.Settings;

namespace DTTS.Services.Database;

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

    private readonly AppDatabaseContext _db;

    /// <summary>リポジトリを生成する。</summary>
    public SettingsRepository(AppDatabaseContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
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
        return Deserialize(json, SettingsJsonContext.Default.FeedbackOptions) ?? new FeedbackOptions();
    }

    /// <summary>フィードバック設定を保存する。</summary>
    public Task SaveFeedbackOptionsAsync(FeedbackOptions options, CancellationToken cancellationToken = default)
        => SetAsync(FeedbackKey, JsonSerializer.Serialize(options, SettingsJsonContext.Default.FeedbackOptions), cancellationToken);

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
