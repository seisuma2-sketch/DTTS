using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace DTTS.Services.Network;

/// <summary>Bluetooth LE (GATT) 通信で使用する UUID とアドレス表記。</summary>
public static class BleProtocol
{
    public static readonly Guid ServiceUuid = new("d7750001-4b3a-4c8e-9b1f-445454530001");
    public static readonly Guid RxCharacteristicUuid = new("d7750002-4b3a-4c8e-9b1f-445454530002");
    public static readonly Guid TxCharacteristicUuid = new("d7750003-4b3a-4c8e-9b1f-445454530003");
    public static readonly Guid InfoCharacteristicUuid = new("d7750004-4b3a-4c8e-9b1f-445454530004");

    public const string AddressPrefix = "ble:";

    public static string FormatAddress(ulong bluetoothAddress) => $"{AddressPrefix}{bluetoothAddress:X12}";

    public static bool TryParseAddress(string? host, out ulong bluetoothAddress)
    {
        bluetoothAddress = 0;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        var trimmed = host.Trim();
        if (!trimmed.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var hex = trimmed.Substring(AddressPrefix.Length);
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bluetoothAddress);
    }
}

/// <summary>
/// Bluetooth LE (GATT) を使った近距離オフライン対戦ネットワークサービス。
/// 周辺端末の探索・ホスト（GATT サーバー）・参加（GATT クライアント）をすべて BLE で行う。
/// （IP アドレスを直接指定したときのみ従来の TCP へフォールバックする）
/// </summary>
public sealed class BleNetworkService : INetworkService
{
    private readonly ISerialDispatcher _dispatcher;
    private readonly string _clientId;

    public BleNetworkService(ISerialDispatcher dispatcher, string clientId)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clientId = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoomInfo>> DiscoverRoomsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported)
        {
            return Array.Empty<RoomInfo>();
        }

        var foundAddresses = new ConcurrentDictionary<ulong, byte>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleProtocol.ServiceUuid);
        Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> receivedHandler =
            (_, args) => foundAddresses.TryAdd(args.BluetoothAddress, 0);
        watcher.Received += receivedHandler;

        try
        {
            watcher.Start();
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            watcher.Received -= receivedHandler;
            try
            {
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BLE watcher stop: {ex.Message}");
            }
        }

        var results = new List<RoomInfo>();
        foreach (ulong addr in foundAddresses.Keys)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var info = await TryReadRoomInfoAsync(addr, cancellationToken).ConfigureAwait(false);
            if (info is not null && info.ProtocolVersion == NetProtocol.Version)
            {
                results.Add(info with { Address = BleProtocol.FormatAddress(addr), Port = 0 });
            }
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<GameHostSession> HostAsync(HostOptions options, CancellationToken cancellationToken = default)
    {
        var session = new GameHostSession(options, _dispatcher);
        var bleHost = await BleGattHost.StartAsync(() => session.CurrentRoomInfo, session.AcceptConnection, cancellationToken).ConfigureAwait(false);
        // 対戦開始後に空き席・切断席がない間は BLE アドバタイズを止めて省電力化する
        session.LobbyChanged += (_, seats) =>
        {
            bool playing = session.CurrentRoomInfo.IsPlaying;
            bool needsAdvertising = !playing || seats.Any(s => s.SeatId != 0 && s.Kind == Core.Enums.PlayerKind.Npc && !s.IsLocked);
            bleHost.SetAdvertising(needsAdvertising);
        };
        session.AttachTransport(bleHost);
        await session.StartAsync(startTcp: false).ConfigureAwait(false);
        return session;
    }

    /// <inheritdoc />
    public async Task<RemoteGameClient> JoinAsync(string host, int port, string playerName, bool alwaysAskInterrupt = false, string? avatarIcon = null, string? joinPin = null, CancellationToken cancellationToken = default)
    {
        if (BleProtocol.TryParseAddress(host, out ulong bluetoothAddress))
        {
            var conn = await BleClientConnection.ConnectAsync(bluetoothAddress, cancellationToken).ConfigureAwait(false);
            return await RemoteGameClient.JoinWithConnectionAsync(conn, playerName, _clientId, _dispatcher,
                timeout: TimeSpan.FromSeconds(8), cancellationToken: cancellationToken, alwaysAskInterrupt: alwaysAskInterrupt, avatarIcon: avatarIcon,
                joinPin: joinPin, endpointKey: host).ConfigureAwait(false);
        }

        // IP アドレスが直接入力された場合のみ従来の TCP へフォールバック
        return await RemoteGameClient.JoinAsync(host, port, playerName, _clientId, _dispatcher,
            cancellationToken: cancellationToken, alwaysAskInterrupt: alwaysAskInterrupt, avatarIcon: avatarIcon, joinPin: joinPin).ConfigureAwait(false);
    }

    private static async Task<RoomInfo?> TryReadRoomInfoAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null) return null;
            var svcRes = await device.GetGattServicesForUuidAsync(BleProtocol.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (svcRes.Status != GattCommunicationStatus.Success || svcRes.Services.Count == 0) return null;
            service = svcRes.Services[0];

            var chRes = await service.GetCharacteristicsForUuidAsync(BleProtocol.InfoCharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (chRes.Status != GattCommunicationStatus.Success || chRes.Characteristics.Count == 0) return null;

            var readRes = await chRes.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (readRes.Status != GattCommunicationStatus.Success || readRes.Value is null) return null;

            byte[] bytes = readRes.Value.ToArray();
            var info = JsonSerializer.Deserialize(bytes, NetJsonContext.Default.RoomInfo);
            if (info is null) return null;
            return info with
            {
                RoomName = SanitizeRoomText(info.RoomName, 64, "DTTS の部屋"),
                HostName = SanitizeRoomText(info.HostName, 32, "ホスト"),
            };
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private static string SanitizeRoomText(string? raw, int maxLen, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }
}

/// <summary>ホスト側 Bluetooth LE GATT サーバー。</summary>
internal sealed class BleGattHost : IAsyncDisposable
{
    private readonly GattServiceProvider _provider;
    private readonly GattLocalCharacteristic _rxChar;
    private readonly GattLocalCharacteristic _txChar;
    private readonly GattLocalCharacteristic _infoChar;
    private readonly Func<RoomInfo> _roomProvider;
    private readonly Action<IMessageConnection> _onAccepted;
    private readonly ConcurrentDictionary<string, BleServerConnection> _clients = new(StringComparer.Ordinal);
    private int _disposed;

    private BleGattHost(
        GattServiceProvider provider,
        GattLocalCharacteristic rxChar,
        GattLocalCharacteristic txChar,
        GattLocalCharacteristic infoChar,
        Func<RoomInfo> roomProvider,
        Action<IMessageConnection> onAccepted)
    {
        _provider = provider;
        _rxChar = rxChar;
        _txChar = txChar;
        _infoChar = infoChar;
        _roomProvider = roomProvider;
        _onAccepted = onAccepted;

        _rxChar.WriteRequested += OnRxWriteRequested;
        _txChar.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _infoChar.ReadRequested += OnInfoReadRequested;
    }

    public static async Task<BleGattHost> StartAsync(
        Func<RoomInfo> roomProvider,
        Action<IMessageConnection> onAccepted,
        CancellationToken cancellationToken)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported)
        {
            throw new InvalidOperationException("この端末では Bluetooth LE が利用できません。Bluetooth をオンにしてください。");
        }
        if (!adapter.IsPeripheralRoleSupported)
        {
            throw new InvalidOperationException("この端末の Bluetooth アダプターはホスト（Peripheral）機能に対応していません。");
        }

        var created = await GattServiceProvider.CreateAsync(BleProtocol.ServiceUuid);
        if (created.Error != BluetoothError.Success || created.ServiceProvider is null)
        {
            throw new InvalidOperationException($"Bluetooth LE サービスの開始に失敗しました（{created.Error}）。");
        }
        var provider = created.ServiceProvider;

        var rxRes = await provider.Service.CreateCharacteristicAsync(
            BleProtocol.RxCharacteristicUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        var txRes = await provider.Service.CreateCharacteristicAsync(
            BleProtocol.TxCharacteristicUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            });
        var infoRes = await provider.Service.CreateCharacteristicAsync(
            BleProtocol.InfoCharacteristicUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            });

        if (rxRes.Error != BluetoothError.Success || txRes.Error != BluetoothError.Success || infoRes.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException("Bluetooth LE キャラクタリスティックの作成に失敗しました。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var host = new BleGattHost(provider, rxRes.Characteristic, txRes.Characteristic, infoRes.Characteristic, roomProvider, onAccepted);
        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true,
        });
        return host;
    }

    private async void OnInfoReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req is null) return;
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(_roomProvider(), NetJsonContext.Default.RoomInfo);
            req.RespondWithValue(json.AsBuffer());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE OnInfoReadRequested: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        var active = sender.SubscribedClients.ToDictionary(c => c.Session.DeviceId.Id, StringComparer.Ordinal);
        foreach (var (deviceId, client) in active)
        {
            GetOrCreateClient(deviceId, client);
        }
        foreach (var existingId in _clients.Keys.ToList())
        {
            if (!active.ContainsKey(existingId) && _clients.TryRemove(existingId, out var conn))
            {
                _ = conn.CloseAsync();
            }
        }
    }

    private async void OnRxWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req is null) return;
            string deviceId = args.Session.DeviceId.Id;
            var subscribed = _txChar.SubscribedClients.FirstOrDefault(c => c.Session.DeviceId.Id == deviceId);
            // 未購読デバイスからの書き込みでクライアントスロットを消費されないよう拒否する
            if (subscribed is null && !_clients.ContainsKey(deviceId))
            {
                if (req.Option == GattWriteOption.WriteWithResponse)
                {
                    req.RespondWithProtocolError(GattProtocolError.WriteNotPermitted);
                }
                return;
            }
            var conn = GetOrCreateClient(deviceId, subscribed);
            if (conn is not null)
            {
                conn.FeedChunk(req.Value.ToArray());
            }
            if (req.Option == GattWriteOption.WriteWithResponse)
            {
                req.Respond();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE OnInfoReadRequested: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private BleServerConnection? GetOrCreateClient(string deviceId, GattSubscribedClient? subscribedClient)
    {
        if (_clients.TryGetValue(deviceId, out var existing))
        {
            if (subscribedClient is not null)
            {
                existing.AttachSubscribedClient(subscribedClient);
            }
            return existing;
        }
        if (_clients.Count >= 12)
        {
            return null;
        }
        var created = new BleServerConnection(deviceId, _txChar, subscribedClient, () => _clients.TryRemove(deviceId, out _));
        if (_clients.TryAdd(deviceId, created))
        {
            _onAccepted(created);
            return created;
        }
        if (_clients.TryGetValue(deviceId, out existing))
        {
            if (subscribedClient is not null)
            {
                existing.AttachSubscribedClient(subscribedClient);
            }
            return existing;
        }
        return null;
    }

    private bool _isAdvertising = true;

    public void SetAdvertising(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0 || _isAdvertising == enabled) return;
        _isAdvertising = enabled;
        try
        {
            if (enabled)
            {
                _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                });
            }
            else
            {
                _provider.StopAdvertising();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE SetAdvertising({enabled}): {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _provider.StopAdvertising();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE StopAdvertising on dispose: {ex.Message}");
        }
        _rxChar.WriteRequested -= OnRxWriteRequested;
        _txChar.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        _infoChar.ReadRequested -= OnInfoReadRequested;

        foreach (var conn in _clients.Values.ToList())
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
        _clients.Clear();
    }
}

/// <summary>ホスト側の 1 ゲスト分の BLE 接続。</summary>
internal sealed class BleServerConnection : IMessageConnection
{
    // BLE の通信路もすべて暗号化する（ホストは Responder：ゲストの Hello を受けてから返す）
    private readonly SecureChannel _secure = new(SecureChannelRole.Responder);
    private readonly GattLocalCharacteristic _txChar;
    private readonly Action _onRemoved;
    private readonly BleFrameAssembler _assembler = new();
    private readonly Channel<NetMessage> _outgoing = Channel.CreateBounded<NetMessage>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private volatile GattSubscribedClient? _subscribedClient;
    private int _closed;
    private int _ctsDisposed;
    private Task? _writeTask;

    public BleServerConnection(string id, GattLocalCharacteristic txChar, GattSubscribedClient? subscribedClient, Action onRemoved)
    {
        Id = id;
        _txChar = txChar;
        _subscribedClient = subscribedClient;
        _onRemoved = onRemoved;
    }

    public string Id { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public string? ShortAuthCode => _secure.ShortAuthCode;
    public event Action<IMessageConnection, NetMessage>? MessageReceived;
    public event Action<IMessageConnection, Exception?>? Closed;

    public void AttachSubscribedClient(GattSubscribedClient client) => _subscribedClient = client;

    public void Start() => _writeTask ??= Task.Run(WriteLoopAsync);

    public bool Send(NetMessage message) => !IsClosed && _outgoing.Writer.TryWrite(message);

    public void FeedChunk(byte[] chunk)
    {
        if (IsClosed) return;
        byte[]? frame;
        lock (_assembler)
        {
            frame = _assembler.Feed(chunk);
        }
        if (frame is null) return;
        try
        {
            if (!_secure.IsEstablished)
            {
                _secure.AcceptHello(frame); // 最初のフレームは鍵交換
                return;
            }
            var msg = _secure.OpenMessage(frame);
            if (msg is not null)
            {
                MessageReceived?.Invoke(this, msg);
            }
        }
        catch (Exception ex)
        {
            Shutdown(ex);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            // ゲストの Hello を受け取って鍵が決まってから、自分の Hello を返す
            await _secure.Established.WaitAsync(SecureChannel.HandshakeTimeout, _cts.Token).ConfigureAwait(false);
            if (!await NotifyFrameAsync(_secure.CreateHello()).ConfigureAwait(false))
            {
                throw new IOException("Bluetooth LE：暗号化の開始を送れませんでした（通知の購読がありません）。");
            }
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await NotifyFrameAsync(_secure.SealMessage(message)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Shutdown(ex);
        }
    }

    /// <summary>1フレームをチャンクに分けて通知で送る（購読中のゲストがいなければ false）。</summary>
    private async Task<bool> NotifyFrameAsync(byte[] frame)
    {
        var client = _subscribedClient;
        if (client is null) return false;
        int maxChunk = Math.Clamp((int)client.Session.MaxPduSize - 3, 20, 480);
        foreach (var chunk in BleChunkCodec.Split(frame, maxChunk))
        {
            await _txChar.NotifyValueAsync(chunk.AsBuffer(), client)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), _cts.Token)
                .ConfigureAwait(false);
        }
        return true;
    }

    public async Task CloseAsync(Exception? reason = null)
    {
        _outgoing.Writer.TryComplete();
        if (_writeTask is not null)
        {
            try { await _writeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        Shutdown(reason);
        if (_writeTask is not null)
        {
            try { await _writeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BLE server write wait: {ex.Message}"); }
        }
        if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
        {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private void Shutdown(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        _assembler.Reset();
        _secure.Abort(reason);
        _secure.Dispose();
        _onRemoved();
        Closed?.Invoke(this, reason);
    }
}

/// <summary>ゲスト側の BLE GATT クライアント接続。</summary>
internal sealed class BleClientConnection : IMessageConnection
{
    // BLE の通信路もすべて暗号化する（ゲストは Initiator：最初に Hello を送る）
    private readonly SecureChannel _secure = new(SecureChannelRole.Initiator);
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _rxChar;
    private readonly GattCharacteristic _txChar;
    private readonly BleFrameAssembler _assembler = new();
    private readonly Channel<NetMessage> _outgoing = Channel.CreateBounded<NetMessage>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;
    private int _ctsDisposed;
    private Task? _writeTask;

    private BleClientConnection(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic rxChar, GattCharacteristic txChar)
    {
        _device = device;
        _service = service;
        _rxChar = rxChar;
        _txChar = txChar;
        Id = device.BluetoothAddress.ToString("X12", CultureInfo.InvariantCulture);
        _txChar.ValueChanged += OnTxValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Id { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public string? ShortAuthCode => _secure.ShortAuthCode;
    public event Action<IMessageConnection, NetMessage>? MessageReceived;
    public event Action<IMessageConnection, Exception?>? Closed;

    public static async Task<BleClientConnection> ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
        if (device is null)
        {
            throw new JoinFailedException("Bluetooth LE デバイスが見つかりませんでした。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var svcRes = await device.GetGattServicesForUuidAsync(BleProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
        if (svcRes.Status != GattCommunicationStatus.Success || svcRes.Services.Count == 0)
        {
            device.Dispose();
            throw new JoinFailedException("DTTS の Bluetooth LE サービスに接続できませんでした。");
        }
        var service = svcRes.Services[0];

        var rxRes = await service.GetCharacteristicsForUuidAsync(BleProtocol.RxCharacteristicUuid, BluetoothCacheMode.Uncached);
        var txRes = await service.GetCharacteristicsForUuidAsync(BleProtocol.TxCharacteristicUuid, BluetoothCacheMode.Uncached);
        if (rxRes.Status != GattCommunicationStatus.Success || rxRes.Characteristics.Count == 0
            || txRes.Status != GattCommunicationStatus.Success || txRes.Characteristics.Count == 0)
        {
            service.Dispose();
            device.Dispose();
            throw new JoinFailedException("Bluetooth LE 通信チャネルの取得に失敗しました。");
        }

        var txChar = txRes.Characteristics[0];
        var cccdStatus = await txChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccdStatus != GattCommunicationStatus.Success)
        {
            service.Dispose();
            device.Dispose();
            throw new JoinFailedException($"Bluetooth LE の通知購読に失敗しました（{cccdStatus}）。");
        }

        return new BleClientConnection(device, service, rxRes.Characteristics[0], txChar);
    }

    public void Start() => _writeTask ??= Task.Run(WriteLoopAsync);

    public bool Send(NetMessage message) => !IsClosed && _outgoing.Writer.TryWrite(message);

    private void OnTxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (IsClosed || args.CharacteristicValue is null) return;
        byte[]? frame;
        lock (_assembler)
        {
            frame = _assembler.Feed(args.CharacteristicValue.ToArray());
        }
        if (frame is null) return;
        try
        {
            if (!_secure.IsEstablished)
            {
                _secure.AcceptHello(frame); // ホストからの Hello（鍵交換の返事）
                return;
            }
            var msg = _secure.OpenMessage(frame);
            if (msg is not null)
            {
                MessageReceived?.Invoke(this, msg);
            }
        }
        catch (Exception ex)
        {
            Shutdown(ex);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Shutdown(null);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            // 鍵交換：まず自分の Hello を送り、ホストの Hello が届くまでメッセージは送らない
            await WriteFrameAsync(_secure.CreateHello()).ConfigureAwait(false);
            await _secure.Established.WaitAsync(SecureChannel.HandshakeTimeout, _cts.Token).ConfigureAwait(false);
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await WriteFrameAsync(_secure.SealMessage(message)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Shutdown(ex);
        }
    }

    /// <summary>1フレームをチャンクに分けて書き込む。</summary>
    private async Task WriteFrameAsync(byte[] frame)
    {
        int maxChunk = Math.Clamp((int)_service.Session.MaxPduSize - 3, 20, 480);
        foreach (var chunk in BleChunkCodec.Split(frame, maxChunk))
        {
            var status = await _rxChar.WriteValueAsync(chunk.AsBuffer(), GattWriteOption.WriteWithResponse)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), _cts.Token)
                .ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                throw new IOException($"Bluetooth LE 送信エラー: {status}");
            }
        }
    }

    public async Task CloseAsync(Exception? reason = null)
    {
        _outgoing.Writer.TryComplete();
        if (_writeTask is not null)
        {
            try { await _writeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        Shutdown(reason);
        if (_writeTask is not null)
        {
            try { await _writeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BLE client write wait: {ex.Message}"); }
        }
        if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
        {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private void Shutdown(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        _assembler.Reset();
        _secure.Abort(reason);
        _secure.Dispose();
        _txChar.ValueChanged -= OnTxValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try { _service.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BLE service dispose: {ex.Message}"); }
        try { _device.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BLE device dispose: {ex.Message}"); }
        Closed?.Invoke(this, reason);
    }
}
