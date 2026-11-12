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
                _voices.Clear();
                foreach (var (effect, nodes) in voices)
                {
                    _voices[effect] = nodes;
                }
                _nextVoice.Clear();
            }
            ApplyGain();
            graph.Start();
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
        lock (_gate)
        {
            bus = _effectsBus;
        }
        if (bus is null)
        {
            return;
        }
        try
        {
            bus.OutgoingGain = _enabled ? SoundEffectCatalog.ToGain(_seVolume) : 0.0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] 音量の設定に失敗しました: {ex.Message}");
        }
    }
}
