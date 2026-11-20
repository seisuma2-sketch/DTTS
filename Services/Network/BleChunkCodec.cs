using System;
using System.Collections.Generic;
using System.IO;

namespace DTTS.Services.Network;

/// <summary>
/// Bluetooth LE (GATT) 用のフレーム分割・再構築ヘルパー。
/// BLE の1パケットあたりの最大長（MTU）制限に収まるよう、大きな JSON フレームを複数チャンクに分けて送受信する。
/// <para>チャンク形式: [Flags (1byte: 0x01=先頭, 0x02=終端)][Seq (1byte)][Payload...]</para>
/// </summary>
public static class BleChunkCodec
{
    public const int HeaderSize = 2;
    public const byte FlagFirst = 0x01;
    public const byte FlagLast = 0x02;

    /// <summary>フレームを指定サイズ以下のチャンク列に分割する。</summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> frame, int maxChunkBytes = 180)
    {
        int maxPayload = Math.Max(1, maxChunkBytes - HeaderSize);
        if (frame.Length == 0)
        {
            return new[] { new byte[] { FlagFirst | FlagLast, 0 } };
        }

        int totalChunks = (frame.Length + maxPayload - 1) / maxPayload;
        var chunks = new List<byte[]>(totalChunks);
        int offset = 0;
        for (int i = 0; i < totalChunks; i++)
        {
            int len = Math.Min(maxPayload, frame.Length - offset);
            byte flags = 0;
            if (i == 0) flags |= FlagFirst;
            if (i == totalChunks - 1) flags |= FlagLast;

            var chunk = new byte[HeaderSize + len];
            chunk[0] = flags;
            chunk[1] = (byte)(i & 0xFF);
            frame.Slice(offset, len).CopyTo(chunk.AsSpan(HeaderSize));
            chunks.Add(chunk);
            offset += len;
        }
        return chunks;
    }
}

/// <summary>
/// 受信した BLE チャンクを順番に結合して元のフレームへ戻すアセンブラ。
/// </summary>
public sealed class BleFrameAssembler
{
    private readonly MemoryStream _buffer = new();
    private bool _assembling;
    private byte _expectedSeq;

    /// <summary>チャンクを1つ投入する。完全なフレームが揃ったらバイト配列を返し、途中なら null を返す。</summary>
    public byte[]? Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < BleChunkCodec.HeaderSize)
        {
            Reset();
            return null;
        }

        byte flags = chunk[0];
        byte seq = chunk[1];
        bool isFirst = (flags & BleChunkCodec.FlagFirst) != 0;
        bool isLast = (flags & BleChunkCodec.FlagLast) != 0;
        var payload = chunk.Slice(BleChunkCodec.HeaderSize);

        if (isFirst)
        {
            Reset();
            _assembling = true;
            _expectedSeq = 0;
        }
        else if (!_assembling || seq != _expectedSeq)
        {
            Reset();
            return null;
        }

        if (_buffer.Length + payload.Length > FrameCodec.MaxFrameBytes)
        {
            Reset();
            return null;
        }

        _buffer.Write(payload);
        _expectedSeq = (byte)((_expectedSeq + 1) & 0xFF);

        if (isLast)
        {
            var result = _buffer.ToArray();
            Reset();
            return result;
        }
        return null;
    }

    public void Reset()
    {
        _buffer.SetLength(0);
        // 最大 1 MiB まで拡張された内部配列を保持し続けないよう容量も解放する
        _buffer.Capacity = 0;
        _assembling = false;
        _expectedSeq = 0;
    }
}
