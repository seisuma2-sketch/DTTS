using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DTTS.Services.Network;

/// <summary>暗号化チャネルでの立場（鍵の向きと、ハンドシェイクの送信順を決める）。</summary>
public enum SecureChannelRole : byte
{
    /// <summary>接続を始めた側（ゲスト）。最初に Hello を送る。</summary>
    Initiator = 0,

    /// <summary>接続を受けた側（ホスト）。相手の Hello を受け取ってから自分の Hello を返す。</summary>
    Responder = 1,
}

/// <summary>
/// 通信ペイロードの暗号化・認証（TCP・Bluetooth LE 共通）。
/// <list type="number">
/// <item><b>ハンドシェイク</b>：接続ごとに使い捨ての ECDH（P-256）鍵ペアを作り、公開鍵と乱数（32バイト）を Hello フレームで交換する。
/// ゲスト（Initiator）が先に送り、ホスト（Responder）は受け取ってから返す（BLE では通知の購読前にホストから送れないため）。</item>
/// <item><b>鍵の導出</b>：共有秘密から HKDF-SHA256 で「ゲスト→ホスト」「ホスト→ゲスト」の2本の AES-256 鍵を作る。
/// salt は双方の乱数、info には双方の公開鍵を含め、ハンドシェイクの内容と鍵を結び付ける。</item>
/// <item><b>メッセージ</b>：AES-GCM で暗号化・改ざん検出する。nonce は「向き＋64ビットの連番」で、同じ鍵で nonce が重複しない。
/// 受信側は連番が1つずつ増えることを確かめ、差し替え・再送・順序の入れ替えを拒否する。</item>
/// </list>
/// <para>
/// 盗聴（手札の覗き見）と改ざんは防げる。ただし公開鍵そのものは認証していないため、通信経路に割り込んで双方になりすます
/// 能動的な攻撃（中間者）までは防げない。参加 PIN・再接続トークンは暗号化後のメッセージで送られるので、経路上では読めない。
/// </para>
/// </summary>
public sealed class SecureChannel : IDisposable
{
    /// <summary>Hello フレームの先頭（平文の JSON や旧版のフレームと取り違えないための識別子）。</summary>
    private static readonly byte[] HelloMagic = "DTTSSEC1"u8.ToArray();

    /// <summary>暗号化フレームの種別バイト。</summary>
    private const byte SealedFrameType = 0xA1;

    private const int NonceSeedLength = 32;
    private const int KeyLength = 32;
    private const int TagLength = 16;
    private const int CounterLength = 8;
    private const int HeaderLength = 1 + CounterLength; // 種別 ＋ 連番（AAD として認証する）

    /// <summary>ハンドシェイクを待つ時間（相手が Hello を送ってこない接続は切る）。</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ECDiffieHellman _ecdh;
    private readonly byte[] _myPublicKey;
    private readonly byte[] _myNonceSeed = RandomNumberGenerator.GetBytes(NonceSeedLength);
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private AesGcm? _sendCipher;
    private AesGcm? _receiveCipher;
    private ulong _sendCounter;
    private ulong _receiveCounter;
    private bool _disposed;

    /// <summary>チャネルを作る（使い捨ての鍵ペアを生成する）。</summary>
    public SecureChannel(SecureChannelRole role)
    {
        Role = role;
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _myPublicKey = _ecdh.ExportSubjectPublicKeyInfo();
        // 失敗したハンドシェイクを誰も待っていなくても、未観測の例外として扱わない
        _established.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>この端末の立場。</summary>
    public SecureChannelRole Role { get; }

    /// <summary>鍵の交換が済み、暗号化して送受信できるか。</summary>
    public bool IsEstablished => _established.Task.IsCompletedSuccessfully;

    /// <summary>鍵の交換が済んだら完了するタスク（失敗・破棄で例外になる）。</summary>
    public Task Established => _established.Task;

    /// <summary>自分の Hello フレーム（平文）。</summary>
    public byte[] CreateHello()
    {
        // [識別子 8][鍵ペアの乱数 32][公開鍵の長さ 2][公開鍵（SubjectPublicKeyInfo）]
        var hello = new byte[HelloMagic.Length + NonceSeedLength + 2 + _myPublicKey.Length];
        var span = hello.AsSpan();
        HelloMagic.CopyTo(span);
        _myNonceSeed.CopyTo(span[HelloMagic.Length..]);
        BinaryPrimitives.WriteUInt16BigEndian(span[(HelloMagic.Length + NonceSeedLength)..], (ushort)_myPublicKey.Length);
        _myPublicKey.CopyTo(span[(HelloMagic.Length + NonceSeedLength + 2)..]);
        return hello;
    }

    /// <summary>フレームが Hello か（先頭の識別子で判定）。</summary>
    public static bool IsHello(ReadOnlySpan<byte> frame) => frame.Length > HelloMagic.Length && frame[..HelloMagic.Length].SequenceEqual(HelloMagic);

    /// <summary>相手の Hello を受け取り、送受信の鍵を導出する。</summary>
    /// <exception cref="InvalidDataException">Hello の形式が不正・二度目の Hello の場合。</exception>
    public void AcceptHello(ReadOnlySpan<byte> frame)
    {
        try
        {
            AcceptHelloCore(frame);
        }
        catch (Exception ex)
        {
            _established.TrySetException(ex);
            throw;
        }
    }

    private void AcceptHelloCore(ReadOnlySpan<byte> frame)
    {
        if (!IsHello(frame) || frame.Length < HelloMagic.Length + NonceSeedLength + 2)
        {
            throw new InvalidDataException("暗号化の開始（Hello）を受け取れませんでした。相手のアプリが古い可能性があります。");
        }
        var peerSeed = frame.Slice(HelloMagic.Length, NonceSeedLength).ToArray();
        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(frame[(HelloMagic.Length + NonceSeedLength)..]);
        var keySpan = frame[(HelloMagic.Length + NonceSeedLength + 2)..];
        if (keyLength == 0 || keyLength > 512 || keySpan.Length != keyLength)
        {
            throw new InvalidDataException("暗号化の開始（Hello）の形式が不正です。");
        }
        var peerPublicKey = keySpan.ToArray();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sendCipher is not null)
            {
                throw new InvalidDataException("暗号化の開始（Hello）を二度受け取りました。");
            }

            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
            if (peer.KeySize != _ecdh.KeySize)
            {
                throw new InvalidDataException("相手の鍵の種類が一致しません。");
            }
            byte[] secret = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);

            // salt・info は「ゲスト → ホスト」の順に並べ、両端で同じ値にする
            bool initiator = Role == SecureChannelRole.Initiator;
            byte[] initiatorSeed = initiator ? _myNonceSeed : peerSeed;
            byte[] responderSeed = initiator ? peerSeed : _myNonceSeed;
            byte[] initiatorKey = initiator ? _myPublicKey : peerPublicKey;
            byte[] responderKey = initiator ? peerPublicKey : _myPublicKey;
            byte[] salt = Concat(initiatorSeed, responderSeed);
            byte[] info = Concat(Encoding.ASCII.GetBytes("DTTS secure channel v1"), initiatorKey, responderKey);
            byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeyLength * 2 + 4, salt, info);
            CryptographicOperations.ZeroMemory(secret);

            var toResponder = okm.AsSpan(0, KeyLength);
            var toInitiator = okm.AsSpan(KeyLength, KeyLength);
            uint sasNum = BinaryPrimitives.ReadUInt32BigEndian(okm.AsSpan(KeyLength * 2, 4)) % 1_000_000;
            ShortAuthCode = sasNum.ToString("D6");
            _sendCipher = new AesGcm(initiator ? toResponder : toInitiator, TagLength);
            _receiveCipher = new AesGcm(initiator ? toInitiator : toResponder, TagLength);
            CryptographicOperations.ZeroMemory(okm);
        }
        _established.TrySetResult();
    }

    /// <summary>鍵交換トランスクリプトから導出した6桁の確認コード（SAS）。両端末で一致すれば MITM がないことを確認できる。</summary>
    public string? ShortAuthCode { get; private set; }

    /// <summary>メッセージを暗号化したフレームにする。</summary>
    public byte[] SealMessage(NetMessage message) => Seal(NetProtocol.Serialize(message));

    /// <summary>暗号化フレームを復号してメッセージにする（解釈できない内容なら null）。</summary>
    /// <exception cref="CryptographicException">改ざん・鍵違い・連番の不一致。</exception>
    public NetMessage? OpenMessage(ReadOnlySpan<byte> frame) => NetProtocol.Deserialize(Open(frame));

    /// <summary>平文を暗号化する。フレーム形式は [種別 1][連番 8][認証タグ 16][暗号文]。</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cipher = _sendCipher ?? throw new InvalidOperationException("暗号化の開始前に送信しようとしました。");
            ulong counter = _sendCounter++;
            var frame = new byte[HeaderLength + TagLength + plaintext.Length];
            frame[0] = SealedFrameType;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1, CounterLength), counter);
            cipher.Encrypt(
                MakeNonce(Role, counter),
                plaintext,
                frame.AsSpan(HeaderLength + TagLength),
                frame.AsSpan(HeaderLength, TagLength),
                frame.AsSpan(0, HeaderLength));
            return frame;
        }
    }

    /// <summary>暗号化フレームを復号する（改ざん・連番違いは <see cref="CryptographicException"/>）。</summary>
    public byte[] Open(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cipher = _receiveCipher ?? throw new InvalidDataException("暗号化の開始前にメッセージを受け取りました。");
            if (frame.Length < HeaderLength + TagLength || frame[0] != SealedFrameType)
            {
                throw new CryptographicException("暗号化されていないフレームを受け取りました。");
            }
            ulong counter = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(1, CounterLength));
            if (counter != _receiveCounter)
            {
                throw new CryptographicException($"フレームの順番が不正です（期待 {_receiveCounter}、受信 {counter}）。");
            }
            var plaintext = new byte[frame.Length - HeaderLength - TagLength];
            // 相手から見た送信の向き（相手の立場）で nonce を作る
            var peerRole = Role == SecureChannelRole.Initiator ? SecureChannelRole.Responder : SecureChannelRole.Initiator;
            cipher.Decrypt(
                MakeNonce(peerRole, counter),
                frame.Slice(HeaderLength + TagLength),
                frame.Slice(HeaderLength, TagLength),
                plaintext,
                frame[..HeaderLength]);
            _receiveCounter++;
            return plaintext;
        }
    }

    /// <summary>ハンドシェイクを失敗として打ち切る（接続が切れたとき、送信側の待機を解く）。</summary>
    public void Abort(Exception? reason = null)
        => _established.TrySetException(reason ?? new IOException("暗号化の開始前に接続が切れました。"));

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
            _sendCipher?.Dispose();
            _receiveCipher?.Dispose();
            _ecdh.Dispose();
        }
        Abort(new ObjectDisposedException(nameof(SecureChannel)));
    }

    /// <summary>nonce（12バイト）＝ 送信側の向き（4バイト）＋ 連番（8バイト）。</summary>
    private static byte[] MakeNonce(SecureChannelRole sender, ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, (uint)sender + 1);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}
