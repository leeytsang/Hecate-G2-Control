using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PeripheralBattery.Windows.Audio;

public sealed record AudioEndpointInfo(string Id, string Name, bool IsHeadset);

/// <summary>
/// Reads the current default playback endpoint on a dedicated STA thread.
/// Deliberately avoids third-party audio libraries so the app only depends on
/// Windows Core Audio and the .NET runtime.
/// </summary>
public sealed class AudioOutputService : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private Exception? _startupError;
    private bool _comInitialized;
    private bool _disposed;

    public AudioOutputService()
    {
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "HecateG2.Audio",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("音频控制器初始化超时");

        if (_startupError is not null)
            throw new InvalidOperationException("无法初始化 Windows 音频控制器", _startupError);
    }

    public AudioEndpointInfo? GetDefaultPlayback() => Invoke(GetDefaultPlaybackCore);

    private void Worker()
    {
        try
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            _comInitialized = hr >= 0;
            if (hr < 0 && hr != RpcChangedMode)
                Marshal.ThrowExceptionForHR(hr);

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
            if (_comInitialized)
            {
                try { CoUninitialize(); } catch { }
            }
        }
    }

    private T Invoke<T>(Func<T> action)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioOutputService));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private static AudioEndpointInfo? GetDefaultPlaybackCore()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(AudioDataFlow.Render, AudioRole.Console, out device) < 0
                || device is null)
                return null;

            if (device.GetId(out string endpointId) < 0 || string.IsNullOrWhiteSpace(endpointId))
                return null;

            string name = GetEndpointFriendlyName(endpointId);
            return new AudioEndpointInfo(
                endpointId,
                name,
                name.Contains("HECATE G2", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(device);
            CoreAudioInterop.ReleaseComObject(enumerator);
        }
    }

    private static string GetEndpointFriendlyName(string endpointId)
    {
        try
        {
            int separator = endpointId.LastIndexOf('.');
            if (separator < 0 || separator + 1 >= endpointId.Length)
                return endpointId;

            string guid = endpointId[(separator + 1)..];
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var properties = baseKey.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\Properties");
            if (properties is null) return endpointId;

            string name = properties.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string ?? "";
            string description = properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string ?? "";
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description))
                return $"{name} ({description})";
            return !string.IsNullOrWhiteSpace(name) ? name
                : !string.IsNullOrWhiteSpace(description) ? description
                : endpointId;
        }
        catch
        {
            return endpointId;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
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
