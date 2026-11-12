using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DTTS.Core.Models;

namespace DTTS.Services.Skin;

/// <summary>
/// スキンパックの情報。
/// <para>
/// パックは1フォルダ＝1パックで、カード画像のファイル名はカードコード（例: <c>S3.png</c>, <c>H10.webp</c>, <c>DQ.jpg</c>,
/// <c>JK0.png</c> / <c>JK1.png</c>、両ジョーカー共通なら <c>JK.png</c>）、裏面は <c>back.png</c>。
/// 任意で <c>skin.json</c>（name / author / version）を置ける。画像が無いカードはベクター描画にフォールバックする。
/// </para>
/// </summary>
/// <param name="Id">パックID（フォルダ名）。</param>
/// <param name="Name">表示名。</param>
/// <param name="Author">作者。</param>
/// <param name="Version">バージョン。</param>
/// <param name="DirectoryPath">フォルダのフルパス。</param>
/// <param name="CardImages">カードID → 画像ファイルパス。</param>
/// <param name="BackImagePath">裏面画像のパス。</param>
public sealed record SkinPackInfo(
    string Id,
    string Name,
    string? Author,
    string? Version,
    string DirectoryPath,
    IReadOnlyDictionary<int, string> CardImages,
    string? BackImagePath)
{
    /// <summary>画像が用意されていないカードの枚数（54枚中）。</summary>
    public int MissingCardCount => (Card.NaturalCardCount + Card.MaxJokerCount) - CardImages.Count;

    /// <summary>画像を1枚も含まない（実質的に使えない）パックか。</summary>
    public bool IsEmpty => CardImages.Count == 0 && BackImagePath is null;
}

/// <summary>skin.json の内容。</summary>
public sealed class SkinManifest
{
    /// <summary>表示名。</summary>
    public string? Name { get; set; }

    /// <summary>作者。</summary>
    public string? Author { get; set; }

    /// <summary>バージョン。</summary>
    public string? Version { get; set; }
}

/// <summary>スキンパックの走査（ファイルシステムのみ使用し、画像のデコードは行わない）。</summary>
public static class SkinPackScanner
{
    /// <summary>対応する画像形式。</summary>
    public static IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>1ファイルの最大サイズ（これを超える画像はメモリ保護のため無視する）。</summary>
    public const long MaxImageFileBytes = 8L * 1024 * 1024;

    /// <summary>マニフェストのファイル名。</summary>
    public const string ManifestFileName = "skin.json";

    /// <summary>ルートフォルダ直下のスキンパックをすべて走査する（画像が1枚もないフォルダは除外）。</summary>
    public static IReadOnlyList<SkinPackInfo> ScanRoot(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return Array.Empty<SkinPackInfo>();
        }
        return Directory.EnumerateDirectories(rootDirectory)
            .Select(ScanPack)
            .Where(p => p is not null && !p.IsEmpty)
            .Select(p => p!)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>1つのフォルダをスキンパックとして走査する。</summary>
    public static SkinPackInfo? ScanPack(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }
        var images = new Dictionary<int, string>();
        string? back = null;
        string? sharedJoker = null;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext))
            {
                continue;
            }
            if (new FileInfo(file).Length > MaxImageFileBytes)
            {
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(file).Trim().ToUpperInvariant();
            if (stem == "BACK")
            {
                back ??= file;
            }
            else if (stem == "JK" || stem == "JOKER")
            {
                sharedJoker ??= file;
            }
            else if (Card.TryParse(stem, out var card))
            {
                images.TryAdd(card.Id, file);
            }
        }

        if (sharedJoker is not null)
        {
            for (int i = 0; i < Card.MaxJokerCount; i++)
            {
                images.TryAdd(Card.Joker(i).Id, sharedJoker);
            }
        }

        var manifest = ReadManifest(Path.Combine(directory, ManifestFileName));
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return new SkinPackInfo(
            id,
            string.IsNullOrWhiteSpace(manifest?.Name) ? id : manifest!.Name!.Trim(),
            manifest?.Author,
            manifest?.Version,
            Path.GetFullPath(directory),
            images,
            back);
    }

    private static SkinManifest? ReadManifest(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), SkinJsonContext.Default.SkinManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// スキンパック（zip）のインストール。
/// パス書き換え攻撃（../ 等）・巨大ファイル・ファイル数過多・対応外ファイルを拒否し、安全にスキンフォルダへ展開する。
/// </summary>
public static class SkinPackInstaller
{
    /// <summary>展開後の合計サイズ上限。</summary>
    public const long MaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>エントリ数の上限。</summary>
    public const int MaxEntries = 128;

    /// <summary>
    /// zip をスキンフォルダへ展開する。zip 内にフォルダが1つだけある構成と、直下に画像がある構成の両方に対応する。
    /// </summary>
    /// <param name="zipPath">zip ファイル。</param>
    /// <param name="skinsRoot">スキンのルートフォルダ。</param>
    /// <param name="packId">パックID（フォルダ名）。null なら zip のファイル名から生成。</param>
    /// <returns>インストールしたパック。</returns>
    /// <exception cref="InvalidDataException">zip の内容が不正な場合。</exception>
    public static SkinPackInfo InstallFromZip(string zipPath, string skinsRoot, string? packId = null)
    {
        var id = MakeSafeId(packId ?? Path.GetFileNameWithoutExtension(zipPath));
        Directory.CreateDirectory(skinsRoot);
        var target = Path.GetFullPath(Path.Combine(skinsRoot, id));
        var staging = target + ".installing";

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);

        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (files.Count == 0)
                {
                    throw new InvalidDataException("zip にファイルが含まれていません。");
                }
                if (files.Count > MaxEntries)
                {
                    throw new InvalidDataException($"ファイル数が多すぎます（上限 {MaxEntries}）。");
                }
                if (files.Sum(e => e.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("展開後のサイズが大きすぎます。");
                }

                foreach (var entry in files)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    bool isManifest = entry.Name.Equals(SkinPackScanner.ManifestFileName, StringComparison.OrdinalIgnoreCase);
                    if (!isManifest && !SkinPackScanner.SupportedExtensions.Contains(ext))
                    {
                        continue; // 対応外ファイルは展開しない
                    }
                    if (entry.Length > SkinPackScanner.MaxImageFileBytes)
                    {
                        continue;
                    }
                    // フォルダ構造は平坦化（ファイル名のみ使用）し、パス書き換えを防ぐ
                    var destination = Path.GetFullPath(Path.Combine(staging, Path.GetFileName(entry.Name)));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"不正なパスを含んでいます: {entry.FullName}");
                    }
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            var info = SkinPackScanner.ScanPack(staging);
            if (info is null || info.IsEmpty)
            {
                throw new InvalidDataException("カード画像が見つかりません（ファイル名は S3.png / HQ.webp / JK.png / back.png の形式）。");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);
            return SkinPackScanner.ScanPack(target)!;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            throw;
        }
    }

    /// <summary>パックを削除する。</summary>
    public static bool Uninstall(string skinsRoot, string packId)
    {
        var target = Path.GetFullPath(Path.Combine(skinsRoot, MakeSafeId(packId)));
        if (!target.StartsWith(Path.GetFullPath(skinsRoot), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))
        {
            return false;
        }
        Directory.Delete(target, recursive: true);
        return true;
    }

    /// <summary>フォルダ名として安全なIDに変換する。</summary>
    public static string MakeSafeId(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '.', ' ' };
        var chars = (name ?? string.Empty).Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var id = new string(chars).Trim('_');
        if (id.Length == 0)
        {
            id = "skin";
        }
        return id.Length > 64 ? id[..64] : id;
    }
}

/// <summary>スキン用の System.Text.Json ソース生成コンテキスト。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SkinManifest))]
internal sealed partial class SkinJsonContext : JsonSerializerContext
{
}
