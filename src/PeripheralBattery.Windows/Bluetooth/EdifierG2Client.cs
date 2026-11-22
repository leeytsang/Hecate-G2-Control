using PeripheralBattery.Core.Audio;
using PeripheralBattery.Core.Protocols;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PeripheralBattery.Windows.Bluetooth;

public sealed record G2BatteryResult(bool Success, int? Percent, string Message);
public sealed record G2EqResult(bool Success, G2EqState? State, string Message);
public sealed record G2EqWriteResult(bool Success, G2EqState? State, string Message);
public sealed record G2SnapshotResult(bool Success, int? Percent, G2EqState? State, string Message);

/// <summary>
/// One G2 control session at a time. Audio stays on the 2.4 GHz receiver; BLE is
/// the telemetry/configuration link used for battery and hardware EQ.
/// </summary>
public sealed class EdifierG2Client : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ulong? _cachedAddress;
    private GattSession? _session;
    private bool? _lastReportedConnection;
    private bool _disposed;

    public event EventHandler? Contacted;
    public event Action<ulong>? AddressDiscovered;
    public event Action<bool>? ConnectionChanged;

    public void SetCachedAddress(ulong? address) => _cachedAddress = address;

    public async Task<G2BatteryResult> ReadBatteryAsync(CancellationToken cancellationToken = default)
    {
        return await RunExclusiveAsync(async session =>
        {
            var response = await session.SendCommandAsync(
                EdifierBleProtocol.BuildBatteryQuery(),
                EdifierBleProtocol.BatteryQueryCommand,
                TimeSpan.FromSeconds(4),
                cancellationToken).ConfigureAwait(false);

            if (!EdifierBleProtocol.TryParseBatteryNotification(response, out var percent))
                return new G2BatteryResult(false, null, "电量响应格式无效");

            Contacted?.Invoke(this, EventArgs.Empty);
            return new G2BatteryResult(true, percent, "BLE 电量读取成功");
        }, ex => new G2BatteryResult(false, null, ex is OperationCanceledException ? "操作已取消" : $"G2 通信失败：{ex.Message}"), cancellationToken).ConfigureAwait(false);
    }

    public async Task<G2EqResult> ReadEqAsync(CancellationToken cancellationToken = default)
    {
        return await RunExclusiveAsync(async session =>
        {
            var state = await QueryEqAsync(session, cancellationToken).ConfigureAwait(false);
            if (state is null) return new G2EqResult(false, null, "设备未回应 EQ 查询");
            Contacted?.Invoke(this, EventArgs.Empty);
            return new G2EqResult(true, state, "硬件 EQ 读取成功");
        }, ex => new G2EqResult(false, null, ex is OperationCanceledException ? "操作已取消" : $"G2 通信失败：{ex.Message}"), cancellationToken).ConfigureAwait(false);
    }

    public async Task<G2SnapshotResult> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await RunExclusiveAsync(async session =>
        {
            int? percent = null;
            G2EqState? state = null;

            try
            {
                var batteryResponse = await session.SendCommandAsync(
                    EdifierBleProtocol.BuildBatteryQuery(),
                    EdifierBleProtocol.BatteryQueryCommand,
                    TimeSpan.FromSeconds(4),
                    cancellationToken).ConfigureAwait(false);
                if (EdifierBleProtocol.TryParseBatteryNotification(batteryResponse, out var value))
                    percent = value;
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            try
            {
                state = await QueryEqAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (percent is not null || state is not null)
                Contacted?.Invoke(this, EventArgs.Empty);

            return new G2SnapshotResult(
                percent is not null || state is not null,
                percent,
                state,
                percent is null && state is null ? "设备未回应电量或 EQ 查询" : "设备状态已同步");
        }, ex => new G2SnapshotResult(false, null, null,
            ex is OperationCanceledException ? "操作已取消" : $"G2 通信失败：{ex.Message}"), cancellationToken).ConfigureAwait(false);
    }
    public async Task<G2EqWriteResult> ApplyEqAsync(
        IReadOnlyList<double> gainsDb,
        CancellationToken cancellationToken = default)
    {
        if (gainsDb.Count != EdifierBleProtocol.G2EqFrequencies.Length)
            return new G2EqWriteResult(false, null, "EQ 必须包含 4 个频段");

        var targetCodes = gainsDb.Select(EdifierBleProtocol.GainDbToCode).ToArray();
        return await RunExclusiveAsync(async session =>
        {
            var previous = await QueryEqAsync(session, cancellationToken).ConfigureAwait(false);
            if (previous is null) return new G2EqWriteResult(false, null, "无法读取当前 EQ，未写入");

            try
            {
                for (var band = 0; band < targetCodes.Length; band++)
                {
                    if (previous.GainCodes[band] == targetCodes[band]) continue;
                    await session.SendCommandAsync(
                        EdifierBleProtocol.BuildEqWriteFrame(band, targetCodes[band]),
                        EdifierBleProtocol.EqWriteCommand,
                        TimeSpan.FromMilliseconds(900),
                        cancellationToken,
                        tolerateTimeout: true).ConfigureAwait(false);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await TryRestoreAsync(session, previous, cancellationToken).ConfigureAwait(false);
                return new G2EqWriteResult(false, null, $"EQ 写入失败：{ex.Message}");
            }

            var verified = await QueryEqAsync(session, cancellationToken).ConfigureAwait(false);
            if (verified is not null && verified.GainCodes.SequenceEqual(targetCodes))
            {
                Contacted?.Invoke(this, EventArgs.Empty);
                return new G2EqWriteResult(true, verified, "EQ 已写入耳机");
            }

            await TryRestoreAsync(session, previous, cancellationToken).ConfigureAwait(false);
            return new G2EqWriteResult(false, null, "写后校验失败，已尝试恢复原 EQ");
        }, ex => new G2EqWriteResult(false, null, ex is OperationCanceledException ? "操作已取消" : $"G2 通信失败：{ex.Message}"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<G2EqState?> QueryEqAsync(GattSession session, CancellationToken cancellationToken)
    {
        var response = await session.SendCommandAsync(
            EdifierBleProtocol.BuildEqReadQuery(),
            EdifierBleProtocol.EqReadCommand,
            TimeSpan.FromSeconds(4),
            cancellationToken).ConfigureAwait(false);

        return EdifierBleProtocol.TryParseEqResponse(response, out var codes)
            ? new G2EqState { GainCodes = codes }
            : null;
    }

    private static async Task TryRestoreAsync(GattSession session, G2EqState previous, CancellationToken cancellationToken)
    {
        try
        {
            for (var band = 0; band < previous.GainCodes.Length; band++)
            {
                await session.SendCommandAsync(
                    EdifierBleProtocol.BuildEqWriteFrame(band, previous.GainCodes[band]),
                    EdifierBleProtocol.EqWriteCommand,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken,
                    tolerateTimeout: true).ConfigureAwait(false);
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort rollback. The caller reports the original failure.
        }
    }

    private async Task<T> RunExclusiveAsync<T>(
        Func<GattSession, Task<T>> action,
        Func<Exception, T> failure,
        CancellationToken cancellationToken)
    {
        if (_disposed) return failure(new ObjectDisposedException(nameof(EdifierG2Client)));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null || !_session.IsConnected)
            {
                if (_session is not null)
                    await DisposeSessionAsync().ConfigureAwait(false);

                var address = _cachedAddress;
                if (address is null)
                    address = await FindAddressAsync(cancellationToken).ConfigureAwait(false);

                if (address is null)
                    return failure(new InvalidOperationException("未发现 EDIFIER BLE 广播"));

                _cachedAddress = address;
                AddressDiscovered?.Invoke(address.Value);

                var session = await GattSession.ConnectAsync(address.Value, cancellationToken).ConfigureAwait(false);
                session.ConnectionChanged += OnSessionConnectionChanged;
                _session = session;
                ReportConnection(true);
            }

            return await action(_session).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            ReportConnection(false);
            return failure(ex);
        }
        catch (Exception ex)
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            ReportConnection(false);
            return failure(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSessionConnectionChanged(bool connected) => ReportConnection(connected);

    private void ReportConnection(bool connected)
    {
        if (_lastReportedConnection == connected) return;
        _lastReportedConnection = connected;
        ConnectionChanged?.Invoke(connected);
    }

    private async Task DisposeSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.ConnectionChanged -= OnSessionConnectionChanged;
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private static async Task<ulong?> FindAddressAsync(CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var ad = args.Advertisement;
            if (string.Equals(ad.LocalName, EdifierBleProtocol.AdvertisingName, StringComparison.OrdinalIgnoreCase)
                || ad.ServiceUuids.Contains(EdifierBleProtocol.AdvertisingServiceUuid))
                found.TrySetResult(args.BluetoothAddress);
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            var completed = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken))
                .ConfigureAwait(false);
            return completed == found.Task ? await found.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnReceived;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisposeSessionAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
    }

    private sealed class GattSession : IAsyncDisposable
    {
        private readonly BluetoothLEDevice _device;
        private readonly GattDeviceService _service;
        private readonly GattCharacteristic _notify;
        private readonly GattCharacteristic _write;
        private readonly SemaphoreSlim _frameAvailable = new(0);
        private readonly Queue<byte[]> _frames = new();
        private readonly List<byte> _buffer = new();
        private readonly object _sync = new();
        private readonly TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> _valueChangedHandler;
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        private GattSession(
            BluetoothLEDevice device,
            GattDeviceService service,
            GattCharacteristic notify,
            GattCharacteristic write)
        {
            _device = device;
            _service = service;
            _notify = notify;
            _write = write;
            _valueChangedHandler = OnValueChanged;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            try { _service.Session.MaintainConnection = true; } catch { }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
            => ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

        public static async Task<GattSession> ConnectAsync(ulong address, CancellationToken cancellationToken)
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null) throw new InvalidOperationException("无法连接 BLE 设备");

            var services = await device.GetGattServicesForUuidAsync(
                EdifierBleProtocol.ServiceUuid,
                BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success)
            {
                device.Dispose();
                throw new InvalidOperationException("未找到 Edifier 控制服务");
            }

            var service = services.Services.FirstOrDefault()
                          ?? throw new InvalidOperationException("Edifier 控制服务为空");
            foreach (var extraService in services.Services)
            {
                if (!ReferenceEquals(extraService, service))
                    extraService.Dispose();
            }
            var notifyResult = await service.GetCharacteristicsForUuidAsync(
                EdifierBleProtocol.NotifyCharacteristicUuid,
                BluetoothCacheMode.Uncached);
            var writeResult = await service.GetCharacteristicsForUuidAsync(
                EdifierBleProtocol.WriteCharacteristicUuid,
                BluetoothCacheMode.Uncached);
            var notify = notifyResult.Characteristics.FirstOrDefault()
                         ?? throw new InvalidOperationException("缺少通知特征");
            var write = writeResult.Characteristics.FirstOrDefault()
                        ?? throw new InvalidOperationException("缺少写入特征");

            var session = new GattSession(device, service, notify, write);
            notify.ValueChanged += session._valueChangedHandler;
            var cccd = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
            {
                await session.DisposeAsync();
                throw new InvalidOperationException("无法订阅设备通知");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }

        public async Task<byte[]> SendCommandAsync(
            byte[] command,
            byte expectedCommand,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool tolerateTimeout = false)
        {
            ClearPending();
            using var writer = new DataWriter();
            writer.WriteBytes(command);
            var option = expectedCommand == EdifierBleProtocol.EqWriteCommand
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;
            var status = await _write.WriteValueAsync(writer.DetachBuffer(), option);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException("BLE 写入失败");

            try
            {
                return await WaitForCommandAsync(expectedCommand, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (tolerateTimeout)
            {
                return [];
            }
        }

        private async Task<byte[]> WaitForCommandAsync(byte command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                byte[]? next = null;
                lock (_sync)
                {
                    while (_frames.Count > 0)
                    {
                        var candidate = _frames.Dequeue();
                        if (candidate.Length > 2 && candidate[2] == command)
                        {
                            next = candidate;
                            break;
                        }
                    }
                }

                if (next is not null) return next;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("设备未回应命令");
                if (!await _frameAvailable.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                    throw new TimeoutException("设备未回应命令");
            }
        }

        private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                lock (_sync)
                {
                    _buffer.AddRange(bytes);
                    ParseFramesLocked();
                }
            }
            catch
            {
                // Ignore malformed notifications; the command timeout reports failure.
            }
        }

        private void ParseFramesLocked()
        {
            while (_buffer.Count >= 6)
            {
                while (_buffer.Count > 0 && _buffer[0] is not (EdifierBleProtocol.DeviceHeader or EdifierBleProtocol.DeviceHeaderLegacy))
                    _buffer.RemoveAt(0);

                if (_buffer.Count < 6) return;
                if (_buffer[1] != EdifierBleProtocol.AppCode)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                var length = (_buffer[3] << 8) | _buffer[4];
                var frameLength = 6 + length;
                if (frameLength > 4096)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }
                if (_buffer.Count < frameLength) return;

                var frame = _buffer.Take(frameLength).ToArray();
                if (frame[^1] != EdifierBleProtocol.Checksum(frame.AsSpan(0, frame.Length - 1)))
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                _frames.Enqueue(frame);
                _frameAvailable.Release();
                _buffer.RemoveRange(0, frameLength);
            }
        }

        private void ClearPending()
        {
            lock (_sync)
            {
                _frames.Clear();
                _buffer.Clear();
            }
            while (_frameAvailable.Wait(0)) { }
        }

        public async ValueTask DisposeAsync()
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            try { _service.Session.MaintainConnection = false; } catch { }
            _notify.ValueChanged -= _valueChangedHandler;
            try
            {
                await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
            _service.Dispose();
            _device.Dispose();
            _frameAvailable.Dispose();
        }
    }
}
