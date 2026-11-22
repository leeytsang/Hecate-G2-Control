using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeripheralBattery.App.Services;
using PeripheralBattery.Core.Audio;
using PeripheralBattery.Core.Devices;
using PeripheralBattery.Windows.Audio;
using PeripheralBattery.Windows.Bluetooth;
using PeripheralBattery.Windows.Services;

namespace PeripheralBattery.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly EdifierG2Client _client = new();
    private readonly EdifierPresenceMonitor _presence = new();
    private readonly AudioOutputService _audio = new();
    private readonly AudioEndpointVisibilityService _endpointVisibility = new();
    private readonly AutoAudioSwitchService _autoSwitch;
    private readonly AppSettingsStore _settingsStore = new();
    private readonly StartupService _startupService = new();
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _suppressPresetSelection;
    private bool _applyingPreset;
    private long _lastBleRetryAt;
    private const long BleRetryIntervalMs = 30_000;
    private bool _suppressBandChanged;
    private bool _autoEqApplying;
    private bool _eqNeedsReapply = true;
    private int? _lastBatteryPercent;
    private double[] _desiredEqGains = [0, 0, 0, 0];

    public MainViewModel()
    {
        _autoSwitch = new AutoAudioSwitchService(_presence, _audio, _endpointVisibility);
        _settings = _settingsStore.Load();
        _client.SetCachedAddress(_settings.HeadsetBluetoothAddress);
        _client.AddressDiscovered += address =>
        {
            _settings.HeadsetBluetoothAddress = address;
            _settingsStore.Save(_settings);
        };
        _client.ConnectionChanged += connected =>
        {
            if (connected) _presence.NoteGattConnected();
            else _presence.NoteGattDisconnected();
        };

        Headset = new DeviceCardViewModel(DeviceCatalog.HeadsetId, DeviceCatalog.HeadsetName)
        {
            ConnectionLabel = "检测中",
            Detail = "正在建立 BLE 控制连接",
        };

        EqBands = new ObservableCollection<EqBandViewModel>(
        [
            new(0, "100 Hz", OnEqBandChanged),
            new(1, "500 Hz", OnEqBandChanged),
            new(2, "2 kHz", OnEqBandChanged),
            new(3, "8 kHz", OnEqBandChanged),
        ]);

        AutoSwitchAudio = _settings.AutoSwitchAudio;
        StartWithWindows = _startupService.IsEnabled();

        if (G2EqPresets.Find(_settings.SelectedEqPresetId) is { } preset)
        {
            SelectedPreset = preset;
        }
        else if (_settings.CustomEqGainsDb is { Length: 4 } custom)
        {
            SetBands(custom);
            _desiredEqGains = custom.ToArray();
        }
        else
        {
            SelectedPreset = G2EqPresets.Neutral;
        }

        _client.Contacted += (_, _) =>
        {
            _autoSwitch.NoteHeadsetContacted();
            Dispatcher.UIThread.Post(() => PresenceText = "耳机已连接");
        };
        _presence.PresenceChanged += present => Dispatcher.UIThread.Post(() => ApplyPresence(present));
        _autoSwitch.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            AutoSwitchStatus = _autoSwitch.LastStatus;
            CurrentOutputName = _autoSwitch.CurrentOutputName ?? "未找到输出设备";
        });
    }

    public DeviceCardViewModel Headset { get; }
    public IReadOnlyList<EqPreset> EqPresets => G2EqPresets.BuiltIn;
    public ObservableCollection<EqBandViewModel> EqBands { get; }

    [ObservableProperty]
    private EqPreset? _selectedPreset;

    [ObservableProperty]
    private bool _autoSwitchAudio;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _currentOutputName = "检测中";

    [ObservableProperty]
    private string _autoSwitchStatus = "等待耳机连接状态";

    [ObservableProperty]
    private string _presenceText = "检测中";

    [ObservableProperty]
    private string _lastUpdatedText = "正在连接耳机…";

    [ObservableProperty]
    private string _statusSummary = "首次读取可能需要几秒钟";

    [ObservableProperty]
    private string _eqStatus = "正在同步耳机 EQ…";

    [ObservableProperty]
    private bool _isDevicePresent;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isEqBusy;

    public bool CanRefresh => !IsRefreshing;
    public bool CanEditEq => !IsEqBusy;
    public bool CanApplyEq => IsDevicePresent && !IsEqBusy;
    public bool IsCustomEq => SelectedPreset is null;
    public string EqModeText => IsCustomEq ? "自定义" : SelectedPreset?.Name ?? "默认";

    partial void OnIsRefreshingChanged(bool value) => OnPropertyChanged(nameof(CanRefresh));

    partial void OnIsEqBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditEq));
        OnPropertyChanged(nameof(CanApplyEq));
    }

    partial void OnIsDevicePresentChanged(bool value) => OnPropertyChanged(nameof(CanApplyEq));

    partial void OnSelectedPresetChanged(EqPreset? value)
    {
        OnPropertyChanged(nameof(IsCustomEq));
        OnPropertyChanged(nameof(EqModeText));
        if (value is null || _suppressPresetSelection || _applyingPreset) return;

        SetBands(value.GainsDb);
        _desiredEqGains = value.GainsDb.ToArray();
        _eqNeedsReapply = true;
        EqStatus = IsDevicePresent
            ? $"已选择「{value.Name}」，点击应用写入耳机"
            : $"已选择「{value.Name}」，耳机开机后将自动应用";
        if (_started) SaveEqSelection(_desiredEqGains);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_started) return;
        if (!_startupService.SetEnabled(value))
            StatusSummary = "无法更新开机自启动设置";
    }

    partial void OnAutoSwitchAudioChanged(bool value)
    {
        if (!_started) return;
        _autoSwitch.SetEnabled(value);
        _settings.AutoSwitchAudio = value;
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void RestoreAudioEndpoints()
    {
        try
        {
            var result = _endpointVisibility.EnsureVisible("手动恢复 HECATE 音频端点");
            AutoSwitchStatus = result.Message;
            CurrentOutputName = _audio.GetDefaultPlayback()?.Name ?? "未找到输出设备";
        }
        catch (Exception ex)
        {
            AutoSwitchStatus = $"恢复端点失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StatusSummary = "正在读取耳机状态…";
        try
        {
            var snapshot = await Task.Run(() => _client.ReadSnapshotAsync(_cts.Token), _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot);
                LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
                StatusSummary = snapshot.Percent is not null
                    ? "电量已同步"
                    : snapshot.State is not null
                        ? "设备已连接，电量等待下一次同步"
                        : "设备未响应，请确认耳机已开机";
            });
            await UpdateAudioStateAsync();
        }
        catch (OperationCanceledException)
        {
            // Application is closing.
        }

        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusSummary = $"读取失败：{ex.Message}";
                LastUpdatedText = "请检查耳机连接";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsRefreshing = false);
        }
    }

    [RelayCommand]
    private async Task ApplyEqAsync()
    {
        if (IsEqBusy) return;

        var gains = EqBands.Select(band => band.GainDb).ToArray();
        _desiredEqGains = gains;
        _eqNeedsReapply = true;
        SaveEqSelection(gains);

        if (!IsDevicePresent)
        {
            EqStatus = "耳机未连接，EQ 已保存；开机后会自动应用";
            return;
        }

        IsEqBusy = true;
        EqStatus = "正在写入硬件 EQ…";
        try
        {
            var result = await Task.Run(() => _client.ApplyEqAsync(gains, _cts.Token), _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    _eqNeedsReapply = false;
                    EqStatus = "EQ 已写入耳机并读回确认";
                    SaveEqSelection(gains);
                }
                else
                {
                    _eqNeedsReapply = true;
                    EqStatus = result.Message;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Application is closing.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _eqNeedsReapply = true;
                EqStatus = $"EQ 写入失败：{ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsEqBusy = false);
        }
    }

    private async Task EnsureDesiredEqAsync()
    {
        if (!IsDevicePresent || IsEqBusy || _autoEqApplying || !_eqNeedsReapply) return;

        _autoEqApplying = true;
        IsEqBusy = true;
        EqStatus = "正在恢复上次 EQ…";
        var target = _desiredEqGains.ToArray();
        try
        {
            var result = await Task.Run(() => _client.ApplyEqAsync(target, _cts.Token), _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    if (target.SequenceEqual(_desiredEqGains))
                    {
                        _eqNeedsReapply = false;
                        EqStatus = "已恢复上次保存的 EQ";
                    }
                    else
                    {
                        _eqNeedsReapply = true;
                        EqStatus = "EQ 目标已更新，点击应用写入耳机";
                    }
                }
                else
                {
                    EqStatus = $"EQ 自动恢复失败：{result.Message}";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Application is closing.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EqStatus = $"EQ 自动恢复失败：{ex.Message}");
        }
        finally
        {
            IsEqBusy = false;
            _autoEqApplying = false;
        }
    }

    [RelayCommand]
    private async Task ResetEqAsync()
    {
        if (IsEqBusy) return;
        _applyingPreset = true;
        SelectedPreset = G2EqPresets.Neutral;
        _applyingPreset = false;
        SetBands(G2EqPresets.Neutral.GainsDb);
        _desiredEqGains = G2EqPresets.Neutral.GainsDb.ToArray();
        _eqNeedsReapply = true;
        await ApplyEqAsync();
    }

    public async Task SwitchEqPresetAsync(string presetId)
    {
        var preset = G2EqPresets.Find(presetId);
        if (preset is null) return;

        SelectedPreset = preset;
        SetBands(preset.GainsDb);
        _desiredEqGains = preset.GainsDb.ToArray();
        _eqNeedsReapply = true;
        await ApplyEqAsync();
    }

    public async Task RunAsync()
    {
        _started = true;
        try
        {
            _autoSwitch.Start(AutoSwitchAudio);
            await RefreshAsync();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            var audioTick = 0;
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshBatteryAsync();
                if (++audioTick % 4 == 0) await UpdateAudioStateAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusSummary = $"后台任务已停止：{ex.Message}");
        }
    }

    private async Task RefreshBatteryAsync()
    {
        try
        {
            // A BLE advertisement can be missed even while a cached GATT
            // endpoint is reachable. Try the cached address at a slower cadence
            // instead of permanently declaring the headset offline.
            var now = Environment.TickCount64;
            if (!_presence.IsPresent && now - _lastBleRetryAt < BleRetryIntervalMs)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MarkHeadsetOffline("耳机已关机或休眠");
                    StatusSummary = "等待耳机开机";
                });
                return;
            }
            _lastBleRetryAt = now;

            var result = await Task.Run(() => _client.ReadBatteryAsync(_cts.Token), _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.Success && result.Percent is { } percent)
                {
                    _lastBatteryPercent = percent;
                    IsDevicePresent = true;
                    Headset.IsConnected = true;
                    Headset.BatteryPercent = percent;
                    Headset.ConnectionLabel = "2.4G + BLE";
                    Headset.Detail = "电量通过 BLE 控制通道读取";
                    LastUpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";
                    StatusSummary = "电量已同步";
                    if (_eqNeedsReapply) _ = EnsureDesiredEqAsync();
                }
                else if (!_presence.IsPresent)
                {
                    MarkHeadsetOffline("耳机已关机或进入休眠");
                    StatusSummary = "等待耳机开机";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusSummary = $"蓝牙状态读取失败：{ex.Message}");
        }
    }

    private async Task UpdateAudioStateAsync()
    {
        try
        {
            var current = await Task.Run(() => _audio.GetDefaultPlayback()?.Name, _cts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentOutputName = current ?? "未找到输出设备";
                if (!string.IsNullOrWhiteSpace(current)
                    && current.Contains("HECATE G2", StringComparison.OrdinalIgnoreCase))
                    AutoSwitchStatus = "当前输出为 HECATE G2";
            });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch

        {
            // Audio metadata is not required for the main controls to work.
        }
    }

    private void ApplySnapshot(G2SnapshotResult snapshot)
    {
        if (snapshot.Percent is not null || snapshot.State is not null)
            IsDevicePresent = true;

        if (snapshot.Percent is { } percent)
        {
            _lastBatteryPercent = percent;
            Headset.IsConnected = true;
            Headset.BatteryPercent = percent;
            Headset.ConnectionLabel = "2.4G + BLE";
            Headset.Detail = snapshot.State is null
                ? "电量通过 BLE 读取，EQ 等待设备响应"
                : "电量与硬件 EQ 通道已连接";
        }
        else if (!_presence.IsPresent)
        {
            MarkHeadsetOffline(snapshot.Message);
        }

        if (snapshot.State is not null)
        {
            var matchesTarget = snapshot.State.GainsDb
                .Zip(_desiredEqGains, (hardware, target) => Math.Abs(hardware - target) < 0.05)
                .All(equal => equal);
            if (matchesTarget)
            {
                _eqNeedsReapply = false;
                EqStatus = "EQ 已与保存的预设同步";
            }
            else
            {
                _eqNeedsReapply = true;
                EqStatus = "设备 EQ 与保存的预设不一致，正在恢复…";
                _ = EnsureDesiredEqAsync();
            }
        }
        else if (Headset.HasBattery)
        {
            EqStatus = "电量已读取，EQ 暂未响应";
        }
    }

    private void ApplyPresence(bool present)
    {
        IsDevicePresent = present;
        PresenceText = present ? "耳机已开机" : "耳机已关机或休眠";
        if (!present)
        {
            _eqNeedsReapply = true;
            MarkHeadsetOffline("耳机已关机或休眠");
            StatusSummary = "耳机已关机";
        }
        else
        {
            Headset.IsConnected = true;
            Headset.Detail = "耳机已开机，正在同步电量与 EQ";
            _ = RefreshBatteryAsync();
            if (_eqNeedsReapply) _ = EnsureDesiredEqAsync();
        }
    }

    private void MarkHeadsetOffline(string reason)
    {
        IsDevicePresent = false;
        _eqNeedsReapply = true;
        Headset.IsConnected = false;
        Headset.BatteryPercent = null;
        Headset.Detail = _lastBatteryPercent is { } last
            ? $"{reason} · 上次电量 {last}%"
            : reason;
    }

    private void OnEqBandChanged(EqBandViewModel _)
    {
        if (_suppressBandChanged) return;
        if (SelectedPreset is not null)
        {
            _suppressPresetSelection = true;
            SelectedPreset = null;
            _suppressPresetSelection = false;
        }
        _desiredEqGains = EqBands.Select(band => band.GainDb).ToArray();
        _eqNeedsReapply = true;
        OnPropertyChanged(nameof(IsCustomEq));
        OnPropertyChanged(nameof(EqModeText));
        EqStatus = IsDevicePresent
            ? "自定义 EQ 已修改，点击应用写入耳机"
            : "自定义 EQ 已保存，耳机开机后自动应用";
    }

    private void SetBands(IReadOnlyList<double> gains)
    {
        _suppressBandChanged = true;
        for (var i = 0; i < EqBands.Count && i < gains.Count; i++)
            EqBands[i].SetGain(gains[i]);
        _suppressBandChanged = false;
    }

    private void SaveEqSelection(IReadOnlyList<double> gains)
    {
        _settings.SelectedEqPresetId = SelectedPreset?.Id ?? "custom";
        _settings.CustomEqGainsDb = gains.ToArray();
        _settingsStore.Save(_settings);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _settings.SelectedEqPresetId = SelectedPreset?.Id ?? "custom";
        _settings.CustomEqGainsDb = _desiredEqGains.ToArray();
        _settings.AutoSwitchAudio = AutoSwitchAudio;
        _settingsStore.Save(_settings);
        _autoSwitch.Dispose();
        _presence.Dispose();
        _client.Dispose();
        _endpointVisibility.Dispose();
        _audio.Dispose();
    }
}
