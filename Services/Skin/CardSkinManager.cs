using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DTTS.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DTTS.Services.Skin;

/// <summary>
/// カード画像の提供。<see cref="GetCardImage"/> が null を返したカードは、呼び出し側（CardControl）がベクター描画で表示する。
/// </summary>
public interface ICardSkinManager
{
    /// <summary>利用可能なスキンパック。</summary>
    IReadOnlyList<SkinPackInfo> AvailablePacks { get; }

    /// <summary>使用中のスキンパック（null ならベクター描画の標準カード）。</summary>
    SkinPackInfo? ActivePack { get; }

    /// <summary>使用するパックが変わった（カード表示の再描画契機）。</summary>
    event EventHandler? ActivePackChanged;

    /// <summary>画像の読み込みに失敗した（そのカードはベクター描画へ切り替える）。</summary>
    event EventHandler<Card>? ImageLoadFailed;

    /// <summary>カード表面の画像（スキンパックのもの）。画像がない・読み込み失敗済みなら null。</summary>
    ImageSource? GetCardImage(Card card);

    /// <summary>
    /// 標準のジョーカーの絵柄（アプリに同梱した Assets\Jokers\joker1.jpg・joker2.jpg）。
    /// スキンパックにジョーカーの画像がないときに使う。ジョーカー以外・画像がない・読み込み失敗済みなら null。
    /// </summary>
    ImageSource? GetBuiltInJokerImage(Card card);

    /// <summary>カード裏面の画像。なければ null。</summary>
    ImageSource? GetBackImage();

    /// <summary>使用するパックを切り替える（null で標準カード）。存在しないIDなら false。</summary>
    bool SetActivePack(string? packId);

    /// <summary>スキンフォルダを再走査する。</summary>
    void Refresh();

    /// <summary>zip からパックをインストールする。</summary>
    SkinPackInfo Install(string zipPath);

    /// <summary>キャッシュした画像を解放する（メモリ逼迫時・画面遷移時）。</summary>
    void ClearCache();
}

/// <summary>
/// スキン画像の管理（WinUI 専用）。
/// <list type="bullet">
/// <item>すべての画像を <see cref="BitmapImage.DecodePixelWidth"/>（既定 280px・物理ピクセル）指定でデコードし、元画像の解像度に関わらずメモリ使用量を抑える。</item>
/// <item>LRU キャッシュ（既定 64 枚）で同じ画像の再デコードを防ぎ、上限を超えたら古いものから解放する。</item>
/// <item>画像がない・読み込みに失敗したカードは null を返し、ベクター描画へ自動フォールバックさせる。</item>
/// </list>
/// UIスレッドから呼び出すこと（BitmapImage はUIスレッドで生成する必要がある）。
/// </summary>
public sealed class CardSkinManager : ICardSkinManager
{
    /// <summary>既定のデコード幅（物理ピクセル）。</summary>
    public const int DefaultDecodePixelWidth = 280;

    /// <summary>既定のキャッシュ上限枚数。</summary>
    public const int DefaultMaxCacheEntries = 64;

    private const string BackKey = "back";

    /// <summary>標準のジョーカー画像のフォルダ（アプリのインストール先からの相対パス）。</summary>
    public const string BuiltInJokerDirectory = "Assets\\Jokers";

    /// <summary>標準のジョーカー画像のパス（1枚目＝joker1.jpg、2枚目＝joker2.jpg）。存在確認済みで、ないものは null。</summary>
    private readonly string?[] _builtInJokerPaths;

    private readonly string _skinsRoot;
    private readonly int _decodePixelWidth;
    private readonly int _maxCacheEntries;
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, BitmapImage Image)> _lru = new();
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private IReadOnlyList<SkinPackInfo> _packs = Array.Empty<SkinPackInfo>();
    private int _generation;

    /// <summary>スキン管理を生成する。</summary>
    /// <param name="skinsRoot">スキンのルートフォルダ（アプリのローカルフォルダ配下を推奨）。</param>
    /// <param name="decodePixelWidth">デコード幅（物理ピクセル、64〜1024）。</param>
    /// <param name="maxCacheEntries">キャッシュ上限枚数。</param>
    /// <param name="builtInJokerDirectory">標準のジョーカー画像のフォルダ（省略時はインストール先の Assets\Jokers）。</param>
    public CardSkinManager(
        string skinsRoot,
        int decodePixelWidth = DefaultDecodePixelWidth,
        int maxCacheEntries = DefaultMaxCacheEntries,
        string? builtInJokerDirectory = null)
    {
        _skinsRoot = skinsRoot ?? throw new ArgumentNullException(nameof(skinsRoot));
        _decodePixelWidth = Math.Clamp(decodePixelWidth, 64, 1024);
        _maxCacheEntries = Math.Max(8, maxCacheEntries);
        Directory.CreateDirectory(_skinsRoot);

        // 存在確認は起動時に1回だけ行う（カードを描くたびにディスクを見に行かない）
        string jokerDir = builtInJokerDirectory ?? Path.Combine(AppContext.BaseDirectory, BuiltInJokerDirectory);
        _builtInJokerPaths = new string?[Card.MaxJokerCount];
        for (int i = 0; i < _builtInJokerPaths.Length; i++)
        {
            string path = Path.Combine(jokerDir, $"joker{i + 1}.jpg");
            _builtInJokerPaths[i] = File.Exists(path) ? path : null;
        }
        Refresh();
    }

    /// <inheritdoc />
    public IReadOnlyList<SkinPackInfo> AvailablePacks => _packs;

    /// <inheritdoc />
    public SkinPackInfo? ActivePack { get; private set; }

    /// <inheritdoc />
    public event EventHandler? ActivePackChanged;

    /// <inheritdoc />
    public event EventHandler<Card>? ImageLoadFailed;

    /// <inheritdoc />
    public void Refresh()
    {
        _packs = SkinPackScanner.ScanRoot(_skinsRoot);
        if (ActivePack is not null)
        {
            var updated = _packs.FirstOrDefault(p => p.Id == ActivePack.Id);
            ApplyPack(updated);
        }
    }

    /// <inheritdoc />
    public bool SetActivePack(string? packId)
    {
        if (packId is null)
        {
            ApplyPack(null);
            return true;
        }
        var pack = _packs.FirstOrDefault(p => p.Id == packId);
        if (pack is null)
        {
            return false;
        }
        ApplyPack(pack);
        return true;
    }

    /// <inheritdoc />
    public SkinPackInfo Install(string zipPath)
    {
        var info = SkinPackInstaller.InstallFromZip(zipPath, _skinsRoot);
        Refresh();
        return info;
    }

    /// <inheritdoc />
    public ImageSource? GetCardImage(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var pack = ActivePack;
        if (pack is null || !pack.CardImages.TryGetValue(card.Id, out var path))
        {
            return null;
        }
        return GetOrLoad(card.Code, path, card);
    }

    /// <inheritdoc />
    public ImageSource? GetBuiltInJokerImage(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.IsJoker || card.JokerIndex < 0 || card.JokerIndex >= _builtInJokerPaths.Length
            || _builtInJokerPaths[card.JokerIndex] is not { } path)
        {
            return null;
        }
        // パックの画像とは別のキーにする（パックのジョーカーが読めなくても、標準の絵柄は使えるように）
        return GetOrLoad("builtin:" + card.Code, path, card);
    }

    /// <inheritdoc />
    public ImageSource? GetBackImage()
    {
        var path = ActivePack?.BackImagePath;
        return path is null ? null : GetOrLoad(BackKey, path, null);
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        _cache.Clear();
        _lru.Clear();
    }

    private void ApplyPack(SkinPackInfo? pack)
    {
        bool changed = !Equals(ActivePack?.Id, pack?.Id) || !ReferenceEquals(ActivePack, pack);
        ActivePack = pack;
        _generation++;
        ClearCache();
        _failed.Clear();
        if (changed)
        {
            ActivePackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ImageSource? GetOrLoad(string key, string path, Card? card)
    {
        if (_failed.Contains(key))
        {
            return null;
        }
        if (_cache.TryGetValue(key, out var node))
        {
            // LRU：最近使ったものを先頭へ
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }

        var image = new BitmapImage
        {
            DecodePixelWidth = _decodePixelWidth,
            DecodePixelType = DecodePixelType.Physical,
        };
        var newNode = _lru.AddFirst((key, image));
        _cache[key] = newNode;
        while (_lru.Count > _maxCacheEntries)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
        }

        _ = LoadAsync(image, key, path, card, _generation);
        return image;
    }

    private async Task LoadAsync(BitmapImage image, string key, string path, Card? card, int generation)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var stream = file.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch (Exception)
        {
            // 破損画像・非対応形式（WebP コーデック未導入等）：以後このカードはベクター描画
            if (generation != _generation)
            {
                return;
            }
            _failed.Add(key);
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _cache.Remove(key);
            }
            if (card is not null)
            {
                ImageLoadFailed?.Invoke(this, card);
            }
        }
    }
}
