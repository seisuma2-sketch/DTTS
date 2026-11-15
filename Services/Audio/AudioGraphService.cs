using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DTTS.Services.Settings;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Storage;

namespace DTTS.Services.Audio;

/// <summary>
/// AudioGraph による効果音再生。
/// <list type="bullet">
/// <item>起動時に全効果音を <see cref="AudioFileInputNode"/> として読み込み、停止状態で待機させる（再生時のファイル読み込み・デコードなし）。</item>
/// <item>再生は先頭へ戻して開始するだけなので、遅延は AudioGraph の1処理単位（最小レイテンシ設定で約10ms）程度。</item>
/// <item>連続する音は複数のボイスを交互に使い、同じ音を重ねて鳴らせる。</item>
/// <item>全効果音はサブミックスノードを通り、その出力ゲインで音量を一括制御する。</item>
/// <item>出力デバイスが無い・切り替わって復旧できない場合は黙って無効化し、次の再生要求で再初期化を試みる。</item>
/// <item>BGM は別のサブミックス（BGM バス）を通り、ファイル入力ノードの無限ループで継ぎ目なく流す。
/// 曲の切り替えは <see cref="BgmMixer"/> の計算でフェードし、フェード中だけ短い間隔のタイマーで音量を進める。</item>
/// </list>
/// </summary>
public sealed class AudioGraphService : IAudioService
{
    private readonly string _audioDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<SoundEffect, AudioFileInputNode[]> _voices = new();
    private readonly Dictionary<SoundEffect, int> _nextVoice = new();
    private AudioGraph? _graph;
    private AudioSubmixNode? _effectsBus;
    private AudioSubmixNode? _bgmBus;
    private readonly Dictionary<BgmTrack, AudioFileInputNode> _bgmNodes = new();
    private readonly BgmMixer _bgmMixer = new();
    private System.Threading.Timer? _fadeTimer;
    private DateTime _lastFadeTick;

    /// <summary>フェード中に音量を進める間隔。</summary>
    private static readonly TimeSpan FadeInterval = TimeSpan.FromMilliseconds(30);
    private Task? _initTask;
    private bool _enabled = true;
    private double _seVolume = 0.8;
    private double _bgmVolume = 0.6;
    private bool _disposed;

    /// <summary>サービスを生成する。</summary>
    /// <param name="audioDirectory">効果音（.wav）のフォルダ。</param>
    public AudioGraphService(string audioDirectory)
    {
        _audioDirectory = audioDirectory ?? throw new ArgumentNullException(nameof(audioDirectory));
    }

    /// <summary>既定のフォルダ（アプリのインストール先の Assets\Audio）で生成する。</summary>
    public static AudioGraphService CreateDefault()
        => new(Path.Combine(AppContext.BaseDirectory, SoundEffectCatalog.RelativeDirectory));

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _graph is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public double SeVolume => _seVolume;

    /// <inheritdoc />
    public double BgmVolume => _bgmVolume;

    /// <inheritdoc />
    public BgmTrack CurrentBgm
    {
        get
        {
            lock (_gate)
            {
                return _bgmMixer.Current;
            }
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }
            return _initTask ??= InitializeCoreAsync();
        }
    }

    /// <inheritdoc />
    public void Play(SoundEffect effect)
    {
        if (!_enabled || _seVolume <= 0.0 || _disposed)
        {
            return;
        }

        AudioFileInputNode? voice = null;
        lock (_gate)
        {
            if (_graph is null)
            {
                // 未初期化・復旧待ち：この音は鳴らさず、次回に向けて準備だけ始める
                _initTask ??= InitializeCoreAsync();
                return;
            }
            if (_voices.TryGetValue(effect, out var nodes) && nodes.Length > 0)
            {
                int index = _nextVoice.TryGetValue(effect, out var n) ? n : 0;
                _nextVoice[effect] = (index + 1) % nodes.Length;
                voice = nodes[index];
            }
        }

        if (voice is null)
        {
            return; // その効果音のファイルが無い
        }
        try
        {
            voice.Stop();
            voice.Seek(TimeSpan.Zero);
            voice.Start();
        }
        catch (Exception ex)
        {
            // 再生失敗でアプリを止めない（デバイス切断直後など）
            Debug.WriteLine($"[Audio] 再生に失敗しました: {effect}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void PlayBgm(BgmTrack track)
    {
        if (_disposed)
        {
            return;
        }
        lock (_gate)
        {
            if (track == _bgmMixer.Current)
            {
                return;
            }
            var start = _bgmMixer.Switch(track);
            if (_graph is null)
            {
                // 準備ができたら、そのとき流すべき曲をフェードインで始める
                _initTask ??= InitializeCoreAsync();
                return;
            }
            if (start is { } t)
            {
                StartBgmNode(t);
            }
            EnsureFadeTimer();
        }
    }

    /// <summary>曲の頭から、音量 0 で鳴らし始める（フェードインはタイマーが進める）。ロック内で呼ぶ。</summary>
    private void StartBgmNode(BgmTrack track)
    {
        if (!_bgmNodes.TryGetValue(track, out var node))
        {
            return;
        }
        try
        {
            node.OutgoingGain = 0.0;
            node.Stop();
            node.Seek(TimeSpan.Zero);
            node.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] BGM を開始できません: {track}: {ex.Message}");
        }
    }

    /// <summary>フェード用のタイマーを動かす（フェードが終われば自分で止まる）。ロック内で呼ぶ。</summary>
    private void EnsureFadeTimer()
    {
        if (!_bgmMixer.IsFading)
        {
            return;
        }
        _lastFadeTick = DateTime.UtcNow;
        _fadeTimer ??= new System.Threading.Timer(_ => OnFadeTick(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _fadeTimer.Change(FadeInterval, FadeInterval);
    }

    private void OnFadeTick()
    {
        lock (_gate)
        {
            if (_disposed || _fadeTimer is null)
            {
                return;
            }
            var now = DateTime.UtcNow;
            var elapsed = now - _lastFadeTick;
            _lastFadeTick = now;
            var changed = _bgmMixer.Advance(elapsed, out var stopped);
            foreach (var track in changed)
            {
                if (_bgmNodes.TryGetValue(track, out var node))
                {
                    try
                    {
                        node.OutgoingGain = _bgmMixer.Level(track);
                    }
                    catch (Exception)
                    {
                        // デバイス切断の直後など。次の初期化でやり直す
                    }
                }
            }
            foreach (var track in stopped)
            {
                if (_bgmNodes.TryGetValue(track, out var node))
                {
                    try
                    {
                        node.Stop();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            if (!_bgmMixer.IsFading)
            {
                _fadeTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
        }
    }

    /// <inheritdoc />
    public void SetSoundEnabled(bool enabled)
    {
        _enabled = enabled;
        ApplyGain();
    }

    /// <inheritdoc />
    public void SetSeVolume(double volume)
    {
        _seVolume = Math.Clamp(double.IsFinite(volume) ? volume : 0.0, 0.0, 1.0);
        ApplyGain();
    }

    /// <inheritdoc />
    public void SetBgmVolume(double volume)
    {
        _bgmVolume = Math.Clamp(double.IsFinite(volume) ? volume : 0.0, 0.0, 1.0);
        ApplyGain();
    }

    /// <inheritdoc />
    public void ApplySettings(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _enabled = settings.SoundEnabled;
        _seVolume = AppearanceSettings.ToUnitVolume(settings.SeVolume);
        _bgmVolume = AppearanceSettings.ToUnitVolume(settings.BgmVolume);
        ApplyGain();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _fadeTimer?.Dispose();
            _fadeTimer = null;
            TearDown();
        }
    }

    // ------------------------------------------------------------------

    private async Task InitializeCoreAsync()
    {
        AudioGraph? graph = null;
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.GameEffects)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
            };
            var created = await AudioGraph.CreateAsync(settings);
            if (created.Status != AudioGraphCreationStatus.Success)
            {
                Debug.WriteLine($"[Audio] AudioGraph を作成できません: {created.Status}");
                ResetInitTask();
                return;
            }
            graph = created.Graph;

            var output = await graph.CreateDeviceOutputNodeAsync();
            if (output.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Debug.WriteLine($"[Audio] 出力デバイスがありません: {output.Status}");
                graph.Dispose();
                ResetInitTask();
                return;
            }

            var bus = graph.CreateSubmixNode();
            bus.AddOutgoingConnection(output.DeviceOutputNode);

            // BGM：別のバスを通し、ファイル入力ノードを無限ループ（継ぎ目なし）にして停止状態で待機させる
            var bgmBus = graph.CreateSubmixNode();
            bgmBus.AddOutgoingConnection(output.DeviceOutputNode);
            var bgmNodes = new Dictionary<BgmTrack, AudioFileInputNode>();
            foreach (var track in Enum.GetValues<BgmTrack>())
            {
                var bgmName = SoundEffectCatalog.BgmFileName(track);
                if (bgmName is null)
                {
                    continue;
                }
                var bgmPath = Path.Combine(_audioDirectory, bgmName);
                if (!File.Exists(bgmPath))
                {
                    Debug.WriteLine($"[Audio] BGM ファイルがありません: {bgmPath}");
                    continue;
                }
                var bgmFile = await StorageFile.GetFileFromPathAsync(bgmPath);
                var bgmNode = await graph.CreateFileInputNodeAsync(bgmFile);
                if (bgmNode.Status != AudioFileNodeCreationStatus.Success)
                {
                    Debug.WriteLine($"[Audio] BGM の読み込みに失敗しました: {bgmPath}: {bgmNode.Status}");
                    continue;
                }
                bgmNode.FileInputNode.Stop();
                bgmNode.FileInputNode.LoopCount = null; // 無限ループ
                bgmNode.FileInputNode.OutgoingGain = 0.0;
                bgmNode.FileInputNode.AddOutgoingConnection(bgmBus);
                bgmNodes[track] = bgmNode.FileInputNode;
            }

            var voices = new Dictionary<SoundEffect, AudioFileInputNode[]>();
            foreach (var effect in Enum.GetValues<SoundEffect>())
            {
                var path = Path.Combine(_audioDirectory, SoundEffectCatalog.FileName(effect));
                if (!File.Exists(path))
                {
                    Debug.WriteLine($"[Audio] 効果音ファイルがありません: {path}");
                    continue;
                }
                var file = await StorageFile.GetFileFromPathAsync(path);
                var nodes = new List<AudioFileInputNode>();
                for (int i = 0; i < SoundEffectCatalog.Polyphony(effect); i++)
                {
                    var node = await graph.CreateFileInputNodeAsync(file);
                    if (node.Status != AudioFileNodeCreationStatus.Success)
                    {
                        Debug.WriteLine($"[Audio] 読み込みに失敗しました: {path}: {node.Status}");
                        break;
                    }
                    // ファイル入力ノードはグラフ開始と同時に再生されるため、停止状態で待機させる
                    node.FileInputNode.Stop();
                    node.FileInputNode.AddOutgoingConnection(bus);
                    nodes.Add(node.FileInputNode);
                }
                voices[effect] = nodes.ToArray();
            }

            graph.UnrecoverableErrorOccurred += OnUnrecoverableError;

            lock (_gate)
            {
                if (_disposed)
                {
                    graph.Dispose();
                    return;
                }
                _graph = graph;
                _effectsBus = bus;
                _bgmBus = bgmBus;
                _bgmNodes.Clear();
                foreach (var (track, node) in bgmNodes)
                {
                    _bgmNodes[track] = node;
                }
                _voices.Clear();
                foreach (var (effect, nodes) in voices)
                {
                    _voices[effect] = nodes;
                }
                _nextVoice.Clear();
            }
            ApplyGain();
            graph.Start();
            lock (_gate)
            {
                // 準備中に指定された曲（または作り直す前に流れていた曲）をフェードインで始める
                if (_bgmMixer.Rewind() is { } current)
                {
                    StartBgmNode(current);
                }
                EnsureFadeTimer();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] 初期化に失敗しました: {ex.Message}");
            graph?.Dispose();
            ResetInitTask();
        }
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        // 出力デバイスの取り外し・既定デバイスの変更など。破棄し、次の再生要求で作り直す
        Debug.WriteLine($"[Audio] 復旧できないエラー: {args.Error}");
        lock (_gate)
        {
            if (ReferenceEquals(sender, _graph))
            {
                TearDown();
                _initTask = null;
            }
        }
    }

    private void ResetInitTask()
    {
        lock (_gate)
        {
            _initTask = null;
        }
    }

    private void TearDown()
    {
        var graph = _graph;
        _graph = null;
        _effectsBus = null;
        _bgmBus = null;
        _bgmNodes.Clear();
        _fadeTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _voices.Clear();
        _nextVoice.Clear();
        if (graph is not null)
        {
            graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            try
            {
                graph.Stop();
            }
            catch (Exception)
            {
            }
            graph.Dispose();
        }
    }

    private void ApplyGain()
    {
        AudioSubmixNode? bus;
        AudioSubmixNode? bgmBus;
        lock (_gate)
        {
            bus = _effectsBus;
            bgmBus = _bgmBus;
        }
        try
        {
            if (bus is not null)
            {
                bus.OutgoingGain = _enabled ? SoundEffectCatalog.ToGain(_seVolume) : 0.0;
            }
            // サウンドが無効なら BGM も完全に無音
            if (bgmBus is not null)
            {
                bgmBus.OutgoingGain = _enabled ? SoundEffectCatalog.ToGain(_bgmVolume) * SoundEffectCatalog.BgmHeadroom : 0.0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] 音量の設定に失敗しました: {ex.Message}");
        }
    }
}
