using System;
using System.Collections.Generic;
using System.Linq;

namespace DTTS.Services.Audio;

/// <summary>
/// BGM のクロスフェードの計算（音声出力に依存しない純粋な状態機械）。
/// 曲ごとに「今の音量（0〜1）」と「目標の音量」を持ち、<see cref="Advance"/> で経過時間ぶん目標へ近づける。
/// 再生側は、変化した曲の音量をノードへ反映し、音量 0 で止まった曲の再生を止めるだけでよい。
/// </summary>
public sealed class BgmMixer
{
    private readonly Dictionary<BgmTrack, double> _level = new();
    private readonly Dictionary<BgmTrack, double> _target = new();
    private readonly List<BgmTrack> _changedScratch = new();
    private readonly List<BgmTrack> _endedScratch = new();
    private readonly List<(BgmTrack Track, double Target)> _targetSnapshot = new();

    /// <summary>新しい曲が鳴り始めるまでの時間。</summary>
    public TimeSpan FadeIn { get; init; } = TimeSpan.FromMilliseconds(1400);

    /// <summary>前の曲が消えるまでの時間。</summary>
    public TimeSpan FadeOut { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>今流そうとしている曲。</summary>
    public BgmTrack Current { get; private set; } = BgmTrack.None;

    /// <summary>フェードの途中か（再生側はこの間だけ時間を進める）。</summary>
    public bool IsFading
    {
        get
        {
            foreach (var (track, target) in _target)
            {
                if (Math.Abs(Level(track) - target) > 1e-9)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>曲の今の音量（0〜1）。</summary>
    public double Level(BgmTrack track) => _level.TryGetValue(track, out var v) ? v : 0.0;

    /// <summary>曲を切り替える。新しい曲を目標 1、それ以外を目標 0 にする。</summary>
    /// <returns>新しく鳴らし始める必要がある曲（すでに鳴っている・None なら null）。</returns>
    public BgmTrack? Switch(BgmTrack track)
    {
        if (track == Current)
        {
            return null;
        }
        Current = track;
        foreach (var t in _level.Keys.Concat(_target.Keys).Distinct().ToList())
        {
            _target[t] = 0.0;
        }
        if (track == BgmTrack.None)
        {
            return null;
        }
        bool wasSilent = Level(track) <= 0.0;
        _target[track] = 1.0;
        _level.TryAdd(track, 0.0);
        return wasSilent ? track : null;
    }

    /// <summary>経過時間ぶんフェードを進める。</summary>
    /// <param name="elapsed">前回からの経過時間。</param>
    /// <param name="stopped">このステップで音量 0 に達した（再生を止めてよい）曲。</param>
    /// <returns>音量が変わった曲。</returns>
    public IReadOnlyList<BgmTrack> Advance(TimeSpan elapsed, out IReadOnlyList<BgmTrack> stopped)
    {
        _changedScratch.Clear();
        _endedScratch.Clear();
        _targetSnapshot.Clear();
        foreach (var kv in _target)
        {
            _targetSnapshot.Add((kv.Key, kv.Value));
        }

        double inStep = FadeIn <= TimeSpan.Zero ? 1.0 : elapsed.TotalMilliseconds / FadeIn.TotalMilliseconds;
        double outStep = FadeOut <= TimeSpan.Zero ? 1.0 : elapsed.TotalMilliseconds / FadeOut.TotalMilliseconds;
        foreach (var (track, target) in _targetSnapshot)
        {
            double now = Level(track);
            if (Math.Abs(now - target) <= 1e-9)
            {
                continue;
            }
            double next = target > now ? Math.Min(target, now + inStep) : Math.Max(target, now - outStep);
            _level[track] = next;
            _changedScratch.Add(track);
            if (next <= 0.0)
            {
                _endedScratch.Add(track);
            }
        }
        stopped = _endedScratch;
        return _changedScratch;
    }

    /// <summary>
    /// すべての曲を音量 0 からやり直す（音声出力を作り直したとき）。今の曲は改めてフェードインする。
    /// </summary>
    /// <returns>鳴らし始める曲（None なら null）。</returns>
    public BgmTrack? Rewind()
    {
        foreach (var t in _level.Keys.ToList())
        {
            _level[t] = 0.0;
        }
        foreach (var t in _target.Keys.ToList())
        {
            _target[t] = t == Current ? 1.0 : 0.0;
        }
        if (Current == BgmTrack.None)
        {
            return null;
        }
        _target[Current] = 1.0;
        return Current;
    }

    /// <summary>すぐに目標の音量へそろえる（フェードなし。サウンドを無効にしたとき・破棄時など）。</summary>
    public void Settle()
    {
        foreach (var (track, target) in _target.ToList())
        {
            _level[track] = target;
        }
    }
}
