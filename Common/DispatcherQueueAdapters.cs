using System;
using System.Collections.Generic;
using DTTS.Core.Ai;
using DTTS.Services.Network;
using Microsoft.UI.Dispatching;

namespace DTTS.Common;

/// <summary>
/// WinUI 3 の <see cref="DispatcherQueue"/> を利用した直列ディスパッチャー。
/// 通信スレッドやバックグラウンド処理からUIスレッドへ操作を安全に引き渡す。
/// </summary>
public sealed class DispatcherQueueSerialDispatcher : ISerialDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueSerialDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcherQueue.TryEnqueue(() => action());
    }
}

/// <summary>
/// WinUI 3 の <see cref="DispatcherQueueTimer"/> を利用したワンショットスケジューラー。
/// ポーリングを行わず、NPCの「考え中」演出の遅延タイマーとして動作する。
/// </summary>
public sealed class DispatcherQueueNpcScheduler : INpcScheduler
{
    private readonly DispatcherQueue _dispatcherQueue;

    // 発火待ちのタイマー（UIスレッドでのみ操作する）。停止時にまとめて止めるために保持する
    private readonly HashSet<DispatcherQueueTimer> _pending = new();
    private bool _stopped;

    public DispatcherQueueNpcScheduler(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public void Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_stopped)
            {
                return;
            }
            if (delay <= TimeSpan.Zero)
            {
                action();
                return;
            }

            var timer = _dispatcherQueue.CreateTimer();
            timer.Interval = delay;
            timer.IsRepeating = false;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _pending.Remove(timer);
                if (!_stopped)
                {
                    action();
                }
            };
            _pending.Add(timer);
            timer.Start();
        });
    }

    /// <summary>
    /// 発火待ちのタイマーをすべて止め、以後の予約も無視する（対戦の終了・画面離脱時）。UIスレッドから呼ぶこと。
    /// </summary>
    public void StopAll()
    {
        _stopped = true;
        foreach (var timer in _pending)
        {
            timer.Stop();
        }
        _pending.Clear();
    }
}

