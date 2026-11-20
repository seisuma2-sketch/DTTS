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
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }
        var images = new Dictionary<int, string>();
        string? back = null;
        string? sharedJoker = null;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext))
            {
                continue;
            }
            if (info.Length > MaxImageFileBytes)
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

                long totalExtractedBytes = 0;
                var buffer = new byte[81920];
                foreach (var entry in files)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    bool isManifest = entry.Name.Equals(SkinPackScanner.ManifestFileName, StringComparison.OrdinalIgnoreCase);
                    if (!isManifest && !SkinPackScanner.SupportedExtensions.Contains(ext))
                    {
                        continue; // 対応外ファイルは展開しない
                    }
                    long maxEntryBytes = isManifest ? 64 * 1024 : SkinPackScanner.MaxImageFileBytes;
                    if (entry.Length > maxEntryBytes)
                    {
                        continue;
                    }
                    // フォルダ構造は平坦化（ファイル名のみ使用）し、パス書き換えを防ぐ
                    var destination = Path.GetFullPath(Path.Combine(staging, Path.GetFileName(entry.Name)));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"不正なパスを含んでいます: {entry.FullName}");
                    }
                    // ZIPヘッダの自己申告サイズではなく、実際の展開バイト数を計測してZIP爆弾を防ぐ
                    using var entryStream = entry.Open();
                    byte[] header = new byte[12];
                    int headerLen = 0;
                    FileStream? outStream = null;
                    try
                    {
                        long entryBytes = 0;
                        int read;
                        while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            bool firstWrite = outStream is null;
                            if (headerLen < header.Length)
                            {
                                int take = Math.Min(header.Length - headerLen, read);
                                Array.Copy(buffer, 0, header, headerLen, take);
                                headerLen += take;
                            }
                            entryBytes += read;
                            totalExtractedBytes += read;
                            if (entryBytes > maxEntryBytes)
                            {
                                throw new InvalidDataException($"ファイルサイズが上限を超えています: {entry.Name}");
                            }
                            if (totalExtractedBytes > MaxTotalBytes)
                            {
                                throw new InvalidDataException("展開後の合計サイズが大きすぎます。");
                            }
                            // 拡張子偽装（非画像ファイルの混入）を防ぐため、ディスクへ書く前に先頭マジックバイトを検証する
                            if (firstWrite)
                            {
                                if (!isManifest && (headerLen >= 12 || read < buffer.Length) && !HasValidImageSignature(ext, header.AsSpan(0, headerLen)))
                                {
                                    break;
                                }
                                if (isManifest || headerLen >= 12 || read < buffer.Length)
                                {
                                    outStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                                    outStream.Write(header, 0, headerLen);
                                    int remaining = read - Math.Min(headerLen, read);
                                    if (remaining > 0)
                                    {
                                        outStream.Write(buffer, read - remaining, remaining);
                                    }
                                }
                                continue;
                            }
                            outStream?.Write(buffer, 0, read);
                        }
                        if (outStream is null && isManifest && headerLen > 0)
                        {
                            outStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                            outStream.Write(header, 0, headerLen);
                        }
                    }
                    finally
                    {
                        outStream?.Dispose();
                    }
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
        var rootFull = Path.GetFullPath(skinsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootFull, MakeSafeId(packId)));
        if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))
        {
            return false;
        }
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }
        Directory.Delete(target, recursive: true);
        return true;
    }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool HasValidImageSignature(string ext, ReadOnlySpan<byte> header) => ext switch
    {
        ".png" => header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
        ".jpg" or ".jpeg" => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
        ".webp" => header.Length >= 12
            && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
            && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P',
        _ => false,
    };

    /// <summary>フォルダ名として安全なIDに変換する（Windows 予約デバイス名も回避）。</summary>
    public static string MakeSafeId(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '.', ' ' };
        var chars = (name ?? string.Empty).Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var id = new string(chars).Trim('_');
        if (id.Length == 0)
        {
            id = "skin";
        }
        if (WindowsReservedNames.Contains(id))
        {
            id = "skin_" + id;
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
