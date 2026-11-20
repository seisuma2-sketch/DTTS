using System;
using System.Collections.Generic;
using System.Linq;

namespace DTTS.Services.Presentation;

/// <summary>定型リアクション1件（通信ではコードだけを送り、表示する文は各端末で引く）。</summary>
/// <param name="Code">通信で送るコード。</param>
/// <param name="Text">吹き出しに出す文。</param>
/// <param name="Glyph">ボタンのアイコン（Segoe Fluent Icons）。</param>
public sealed record QuickReaction(string Code, string Text, string Glyph);

/// <summary>
/// 対戦中に送れる定型リアクション。自由文は送らない（悪口・個人情報を流せないようにし、ホストはコードを確かめるだけでよい）。
/// </summary>
public static class QuickReactions
{
    /// <summary>送れるリアクション（並び順はボタンの順）。</summary>
    public static IReadOnlyList<QuickReaction> All { get; } = new[]
    {
        new QuickReaction("nice", "ナイス！", ""),
        new QuickReaction("thanks", "ありがとう", ""),
        new QuickReaction("sorry", "ごめん！", ""),
        new QuickReaction("hmm", "うーん…", ""),
        new QuickReaction("wow", "すごい！", ""),
        new QuickReaction("gg", "おつかれさま", ""),
    };

    /// <summary>自由入力ひとことの最大文字数。</summary>
    public const int MaxCustomTextLength = 40;

    /// <summary>自由入力のプレフィックス。</summary>
    public const string CustomPrefix = ":";

    /// <summary>知っているコードか（通信で受け取ったコードの確認）。</summary>
    public static bool IsKnown(string? code) => code is not null && All.Any(r => string.Equals(r.Code, code, StringComparison.Ordinal));

    /// <summary>コードに対応する文（知らないコードは null）。</summary>
    public static string? TextOf(string? code) => All.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal))?.Text;

    /// <summary>自由入力テキストを安全な送信ペイロード（":" + 本文）に変換する。</summary>
    public static bool TryEncodeCustom(string? raw, out string payload)
    {
        var clean = SanitizeCustom(raw);
        if (clean is null)
        {
            payload = "";
            return false;
        }
        payload = CustomPrefix + clean;
        return true;
    }

    /// <summary>定型コードまたは有効な自由入力ペイロードかを判定する。</summary>
    public static bool IsValidPayload(string? codeOrPayload)
        => IsKnown(codeOrPayload) || TryDecodeCustom(codeOrPayload, out _);

    /// <summary>定型コードまたは自由入力ペイロードを表示用テキストに変換する。</summary>
    public static string? DisplayTextOf(string? codeOrPayload)
    {
        if (TextOf(codeOrPayload) is { } preset) return preset;
        return TryDecodeCustom(codeOrPayload, out var custom) ? custom : null;
    }

    private static bool TryDecodeCustom(string? payload, out string text)
    {
        if (payload is not null && payload.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            var clean = SanitizeCustom(payload[CustomPrefix.Length..]);
            if (clean is not null)
            {
                text = clean;
                return true;
            }
        }
        text = "";
        return false;
    }

    private static string? SanitizeCustom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxCustomTextLength ? clean[..MaxCustomTextLength] : clean;
    }
}
