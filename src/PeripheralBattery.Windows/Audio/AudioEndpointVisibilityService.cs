using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PeripheralBattery.Core.Devices;

namespace PeripheralBattery.Windows.Audio;

public sealed record HecateAudioEndpoint(
    string Id,
    string Flow,
    string Name,
    string DeviceInstanceId,
    int RegistryState)
{
    public bool IsRegistryActive => RegistryState == AudioEndpointVisibilityService.RegistryActive;

    public bool IsRegistryDisabled => RegistryState == AudioEndpointVisibilityService.RegistryDisabled;
}

public sealed record EndpointVisibilitySnapshot(
    IReadOnlyList<HecateAudioEndpoint> Endpoints,
    bool AllVisible,
    bool AllHidden,
    bool HasUnknownState)
{
    public bool HasEndpoints => Endpoints.Count > 0;
}

public sealed record EndpointVisibilityResult(
    bool Success,
    string Message,
    IReadOnlyList<HecateAudioEndpoint> Endpoints)
{
    public static EndpointVisibilityResult Fail(string message, IReadOnlyList<HecateAudioEndpoint>? endpoints = null) =>
        new(false, message, endpoints ?? Array.Empty<HecateAudioEndpoint>());
}

/// <summary>
/// Hides or shows only the USB Audio endpoints that belong to the HECATE G2
/// receiver. This intentionally does not touch Bluetooth endpoints or PnP.
/// Every write is verified independently through MMDeviceEnumerator and the
/// registry, and a failed hide is rolled back to visible immediately.
/// </summary>
public sealed class AudioEndpointVisibilityService : IDisposable
{
    public const int RegistryActive = 0x00000001;
    public const int RegistryDisabled = 0x10000001;

    private static readonly string TargetInstancePrefix = DeviceCatalog.HecateReceiverInstancePrefix;
    private const string AudioRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string NameValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string DeviceInstanceValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";

    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private IPolicyConfig? _policy;
    private Exception? _startupError;
    private bool _comInitialized;
    private bool _disposed;

    public AudioEndpointVisibilityService()
    {
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "HecateG2.EndpointVisibility",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("端点可见性接口初始化超时");

        if (_startupError is not null)
            throw new InvalidOperationException("无法初始化 Windows 端点可见性接口", _startupError);
    }

    public EndpointVisibilitySnapshot GetSnapshot()
    {
        lock (_gate)
            return Invoke(GetSnapshotCore);
    }

    public EndpointVisibilityResult EnsureVisible(string reason) =>
        ApplyVisible(true, reason, rollbackOnFailure: false);

    public EndpointVisibilityResult Hide(string reason) =>
        ApplyVisible(false, reason, rollbackOnFailure: true);

    public EndpointVisibilityResult Show(string reason) =>
        ApplyVisible(true, reason, rollbackOnFailure: false);

    private EndpointVisibilityResult ApplyVisible(bool visible, string reason, bool rollbackOnFailure)
    {
        lock (_gate)
        {
            if (_disposed)
                return EndpointVisibilityResult.Fail("端点可见性服务已关闭");

            return Invoke(() => ApplyVisibleCore(visible, reason, rollbackOnFailure));
        }
    }

    private void Worker()
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            _comInitialized = hr >= 0;
            if (hr < 0 && hr != RpcChangedMode)
                Marshal.ThrowExceptionForHR(hr);

            _policy = (IPolicyConfig)new PolicyConfigClient();
            _ready.Set();

            foreach (var action in _work.GetConsumingEnumerable())
                action();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(_policy);
            _policy = null;
            if (_comInitialized)
            {
                try { CoUninitialize(); } catch { }
            }
        }
    }

    private T Invoke<T>(Func<T> action)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioEndpointVisibilityService));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private EndpointVisibilitySnapshot GetSnapshotCore()
    {
        var endpoints = EnumerateTargets();
        if (endpoints.Count == 0)
            return new(endpoints, false, false, true);

        var allVisible = endpoints.All(endpoint =>
            endpoint.IsRegistryActive && IsActiveInCoreAudio(endpoint.Id));
        var allHidden = endpoints.All(endpoint =>
            endpoint.IsRegistryDisabled && !IsActiveInCoreAudio(endpoint.Id));
        var hasUnknown = endpoints.Any(endpoint => !endpoint.IsRegistryActive && !endpoint.IsRegistryDisabled);

        return new(endpoints, allVisible, allHidden, hasUnknown);
    }

    private EndpointVisibilityResult ApplyVisibleCore(bool visible, string reason, bool rollbackOnFailure)
    {
        if (_policy is null)
            return EndpointVisibilityResult.Fail("Windows 端点策略接口不可用");

        var before = EnumerateTargets();
        if (before.Count == 0)
        {
            return EndpointVisibilityResult.Fail(
                "未找到 HECATE G2 的 USB 音频端点，保持现状");
        }

        if (before.Any(endpoint => !endpoint.IsRegistryActive && !endpoint.IsRegistryDisabled))
        {
            return EndpointVisibilityResult.Fail(
                "HECATE G2 端点状态未知，拒绝隐藏或恢复并保持现状",
                before);
        }

        var alreadyDesired = visible
            ? before.All(endpoint => endpoint.IsRegistryActive)
            : before.All(endpoint => endpoint.IsRegistryDisabled);

        if (alreadyDesired && VerifyVisible(before, visible))
        {
            return new(true, visible ? "HECATE G2 端点已可见" : "HECATE G2 端点已隐藏", before);
        }

        var calls = 0;
        var errors = new List<string>();
        foreach (var endpoint in before)
        {
            if (visible ? endpoint.IsRegistryActive : endpoint.IsRegistryDisabled)
                continue;

            var hr = _policy.SetEndpointVisibility(endpoint.Id, visible ? 1 : 0);
            if (hr >= 0)
                calls++;
            else
                errors.Add($"{endpoint.Flow}:0x{hr:X8}");
        }

        var after = WaitForVisibility(before.Count, visible);
        var verified = after.Count == before.Count && VerifyVisible(after, visible);
        if (verified)
        {
            return new(
                true,
                visible
                    ? $"已显示 HECATE G2 端点（{calls} 次写入，{reason}）"
                    : $"已隐藏 HECATE G2 端点（{calls} 次写入，{reason}）",
                after);
        }

        var failure = errors.Count > 0
            ? $"Windows 端点策略调用失败：{string.Join(", ", errors)}"
            : visible
                ? "显示端点后复核失败"
                : "隐藏端点后未落到“已禁用”，复核失败";

        if (!visible && rollbackOnFailure)
        {
            var rollbackErrors = new List<string>();
            foreach (var endpoint in after)
            {
                if (endpoint.IsRegistryDisabled)
                {
                    var hr = _policy.SetEndpointVisibility(endpoint.Id, 1);
                    if (hr < 0)
                        rollbackErrors.Add($"{endpoint.Flow}:0x{hr:X8}");
                }
            }

            var recovered = WaitForVisibility(after.Count, true);
            var rollbackOk = recovered.Count > 0 && VerifyVisible(recovered, true);
            var suffix = rollbackErrors.Count > 0
                ? $"，回滚调用错误：{string.Join(", ", rollbackErrors)}"
                : string.Empty;
            failure += rollbackOk
                ? "；已自动恢复为可见"
                : $"；自动恢复失败，请运行程序点击“恢复音频端点”或重启 Windows{suffix}";
            return new(false, failure, recovered);
        }

        return new(false, failure, after);
    }

    private static List<HecateAudioEndpoint> WaitForVisibility(int expectedCount, bool visible)
    {
        var deadline = Environment.TickCount64 + 1500;
        List<HecateAudioEndpoint> endpoints;
        do
        {
            endpoints = EnumerateTargets();
            if (endpoints.Count == expectedCount && VerifyVisible(endpoints, visible))
                return endpoints;
            Thread.Sleep(75);
        }
        while (Environment.TickCount64 < deadline);

        return EnumerateTargets();
    }
    private static bool VerifyVisible(IReadOnlyList<HecateAudioEndpoint> endpoints, bool visible)
    {
        if (endpoints.Count == 0)
            return false;

        foreach (var endpoint in endpoints)
        {
            var active = IsActiveInCoreAudio(endpoint.Id);
            if (visible)
            {
                if (!endpoint.IsRegistryActive || !active)
                    return false;
            }
            else
            {
                if (!endpoint.IsRegistryDisabled || active)
                    return false;
            }
        }

        return true;
    }

    private static bool IsActiveInCoreAudio(string endpointId)
    {
        try { return CoreAudioInterop.IsInState(endpointId, AudioDeviceState.Active); }
        catch { return false; }
    }

    private static List<HecateAudioEndpoint> EnumerateTargets()
    {
        var result = new List<HecateAudioEndpoint>();
        AddFlow(result, "Render", 0);
        AddFlow(result, "Capture", 1);
        return result
            .OrderBy(endpoint => endpoint.Flow, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFlow(List<HecateAudioEndpoint> result, string flow, int flowIndex)
    {
        var flowPath = $@"{AudioRegistryPath}\{flow}";
        using var flowKey = Registry.LocalMachine.OpenSubKey(flowPath);
        if (flowKey is null)
            return;

        foreach (var endpointKeyName in flowKey.GetSubKeyNames())
        {
            using var endpointKey = flowKey.OpenSubKey(endpointKeyName);
            using var propertiesKey = endpointKey?.OpenSubKey("Properties");
            if (endpointKey is null || propertiesKey is null)
                continue;

            var deviceInstance = NormalizeInstanceId(propertiesKey.GetValue(DeviceInstanceValue) as string);
            if (string.IsNullOrWhiteSpace(deviceInstance)
                || !deviceInstance.StartsWith(TargetInstancePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var state = endpointKey.GetValue("DeviceState") is int value ? value : 0;
            var name = propertiesKey.GetValue(NameValue) as string;
            var id = $"{{0.0.{flowIndex}.00000000}}.{endpointKeyName.ToUpperInvariant()}";
            result.Add(new(
                id,
                flow,
                string.IsNullOrWhiteSpace(name) ? "HECATE G2" : name,
                deviceInstance,
                state));
        }
    }

    private static string NormalizeInstanceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var close = value.IndexOf('}');
        return close >= 0 && close + 1 < value.Length
            ? value[(close + 1)..].TrimStart('.')
            : value;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                if (_policy is not null)
                    Invoke(() => ApplyVisibleCore(true, "程序退出安全恢复", rollbackOnFailure: false));
            }
            catch
            {
                // Best effort. A missing UI action is less dangerous than
                // leaving a hidden endpoint behind if COM is already gone.
            }

            _disposed = true;
            _work.CompleteAdding();
        }

        _thread.Join(TimeSpan.FromSeconds(3));
        _work.Dispose();
        _ready.Dispose();
    }

    private const uint CoInitApartmentThreaded = 0x2;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
