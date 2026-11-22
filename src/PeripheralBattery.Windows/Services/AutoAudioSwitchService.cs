using PeripheralBattery.Windows.Audio;
using PeripheralBattery.Windows.Bluetooth;

namespace PeripheralBattery.Windows.Services;

/// <summary>
/// Follows the headset's BLE control-link presence and makes the always-present
/// 2.4 GHz audio endpoints behave like a real Bluetooth endpoint:
/// connected => endpoints visible, disconnected => endpoints hidden.
/// Endpoint visibility is the only switching action; Windows decides which
/// output device becomes active.
/// </summary>
public sealed class AutoAudioSwitchService : IDisposable
{
    private readonly object _gate = new();
    private readonly EdifierPresenceMonitor _presence;
    private readonly AudioOutputService _audio;
    private readonly AudioEndpointVisibilityService _visibility;
    private bool _enabled;
    private bool _disposed;
    private bool? _lastAppliedVisible;

    public AutoAudioSwitchService(
        EdifierPresenceMonitor presence,
        AudioOutputService audio,
        AudioEndpointVisibilityService visibility)
    {
        _presence = presence;
        _audio = audio;
        _visibility = visibility;
    }

    public event Action? StateChanged;

    public string LastStatus { get; private set; } = "等待耳机连接状态";
    public bool IsHeadsetPresent => _presence.IsPresent;
    public string? CurrentOutputName
    {
        get
        {
            try
            {
                return _audio.GetDefaultPlayback()?.Name;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Start(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            _lastAppliedVisible = null;
        }

        _presence.PresenceChanged += OnPresenceChanged;

        var restore = EnsureVisibleSafely("程序启动时恢复 HECATE 音频端点");
        if (!restore.Success)
            SetStatus($"启动恢复端点失败：{restore.Message}");

        try
        {
            _presence.Start();
        }
        catch (Exception ex)
        {
            SetStatus($"启动蓝牙状态监听失败：{ex.Message}");
        }

        if (!enabled)
        {
            SetStatus("自动显隐已关闭 · 端点保持显示");
            return;
        }

        if (_presence.IsPresent)
            ApplyVisible(true, "启动时检测到耳机已连接");
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            EndpointVisibilityResult result;
            lock (_gate)
            {
                if (_disposed) return;
                _enabled = false;
                _lastAppliedVisible = true;
                result = EnsureVisibleSafely("关闭自动显隐");
            }

            SetStatus(result.Success ? "自动显隐已关闭 · 端点已显示" : $"恢复端点失败：{result.Message}");
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _enabled = true;
            _lastAppliedVisible = null;
        }

        ApplyVisible(_presence.IsPresent,
            _presence.IsPresent ? "已启用自动显隐，耳机在线" : "已启用自动显隐，耳机离线");
    }

    public void NoteHeadsetContacted() => _presence.NoteSeen();

    private void OnPresenceChanged(bool present)
    {
        if (!IsEnabled()) return;
        ApplyVisible(present, present ? "耳机已连接 BLE 控制链路" : "耳机 BLE 控制链路已断开");
    }

    private bool IsEnabled()
    {
        lock (_gate) return !_disposed && _enabled;
    }

    private void ApplyVisible(bool visible, string reason)
    {
        EndpointVisibilityResult result;
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            if (_lastAppliedVisible == visible)
                return;

            _lastAppliedVisible = visible;
            result = visible
                ? ShowSafely(reason)
                : HideSafely(reason);

            if (!result.Success)
                _lastAppliedVisible = null;
        }

        if (result.Success)
        {
            SetStatus(visible
                ? "耳机已连接 · 2.4G 音频端点已显示"
                : "耳机未连接 · 2.4G 音频端点已隐藏");
        }
        else
        {
            SetStatus($"{(visible ? "显示" : "隐藏")}端点失败：{result.Message}");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(700).ConfigureAwait(false);
            StateChanged?.Invoke();
        });
    }

    private EndpointVisibilityResult EnsureVisibleSafely(string reason)
    {
        try
        {
            return _visibility.EnsureVisible(reason);
        }
        catch (Exception ex)
        {
            return EndpointVisibilityResult.Fail($"端点服务异常：{ex.Message}");
        }
    }

    private EndpointVisibilityResult ShowSafely(string reason)
    {
        try
        {
            return _visibility.Show(reason);
        }
        catch (Exception ex)
        {
            return EndpointVisibilityResult.Fail($"端点服务异常：{ex.Message}");
        }
    }

    private EndpointVisibilityResult HideSafely(string reason)
    {
        try
        {
            return _visibility.Hide(reason);
        }
        catch (Exception ex)
        {
            return EndpointVisibilityResult.Fail($"端点服务异常：{ex.Message}");
        }
    }
    private void SetStatus(string status)
    {
        LastStatus = status;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _presence.PresenceChanged -= OnPresenceChanged;
    }
}
