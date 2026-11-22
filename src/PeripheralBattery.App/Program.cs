using System;
using System.Threading;
using Avalonia;

namespace PeripheralBattery.App;

sealed class Program
{
    private const string SingleInstanceName = @"Local\HecateG2Control.SingleInstance";
    private const string ShowWindowEventName = @"Local\HecateG2Control.ShowWindow";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;

    [STAThread]
    public static void Main(string[] args)
    {
        _instanceMutex = new Mutex(true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showEvent.Set();
            }
            catch
            {
                // The existing instance is not ready yet.
            }
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        App.ShowRequestedEvent = _showEvent;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _showEvent.Dispose();
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
