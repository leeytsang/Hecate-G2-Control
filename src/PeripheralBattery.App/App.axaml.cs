using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using PeripheralBattery.App.ViewModels;
using PeripheralBattery.App.Views;
using PeripheralBattery.Core.Audio;

namespace PeripheralBattery.App;

public partial class App : Application
{
    public static EventWaitHandle? ShowRequestedEvent { get; set; }
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _batteryMenuItem;
    private NativeMenuItem? _eqStatusMenuItem;
    private readonly List<(EqPreset Preset, NativeMenuItem Item)> _eqMenuItems = new();
    private bool _trayAvailable;
    private bool _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _viewModel = new MainViewModel();
            _viewModel.Headset.PropertyChanged += OnHeadsetPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _window = new MainWindow { DataContext = _viewModel };
            _window.Closing += OnMainWindowClosing;
            desktop.MainWindow = _window;

            CreateTrayIcon(desktop);

            if (ShowRequestedEvent is { } showEvent)
            {
                _ = Task.Run(() =>
                {
                    while (!_isExiting)
                    {
                        showEvent.WaitOne();
                        Dispatcher.UIThread.Post(ShowMainWindow);
                    }
                });
            }

            var startHidden = Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
            if (startHidden)
            {
                _window.ShowInTaskbar = false;
                Dispatcher.UIThread.Post(() =>
                {
                    _window.Hide();
                    _window.ShowInTaskbar = true;
                });
            }

            _ = _viewModel.RunAsync();
            desktop.Exit += (_, _) =>
            {
                _isExiting = true;
                if (_viewModel is not null)
                {
                    _viewModel.Headset.PropertyChanged -= OnHeadsetPropertyChanged;
                    _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                }
                _trayIcon?.Dispose();
                _trayIcon = null;
                _viewModel?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (_viewModel is null) return;

            _batteryMenuItem = new NativeMenuItem
            {
                Header = "电量：--",
                IsEnabled = false,
            };
            _eqStatusMenuItem = new NativeMenuItem
            {
                Header = "当前 EQ：默认",
                IsEnabled = false,
            };

            var eqMenu = new NativeMenu();
            foreach (var preset in _viewModel.EqPresets)
            {
                var captured = preset;
                var item = new NativeMenuItem { Header = captured.Name };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await _viewModel.SwitchEqPresetAsync(captured.Id);
                    }
                    catch
                    {
                        // The main window exposes detailed EQ errors.
                    }
                    UpdateTrayStatus();
                };
                _eqMenuItems.Add((captured, item));
                eqMenu.Items.Add(item);
            }

            var eqRoot = new NativeMenuItem
            {
                Header = "EQ 预设",
                Menu = eqMenu,
            };
            var openItem = new NativeMenuItem { Header = "打开 HECATE G2 控制台" };
            openItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowMainWindow);
            var exitItem = new NativeMenuItem { Header = "退出" };
            exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _isExiting = true;
                desktop.Shutdown();
            });

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://HecateG2Control/Assets/app.ico"))),
                ToolTipText = "HECATE G2 控制台",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        _batteryMenuItem,
                        _eqStatusMenuItem,
                        eqRoot,
                        openItem,
                        exitItem,
                    }
                },
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowMainWindow);
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            _trayAvailable = true;
            UpdateTrayStatus();
        }
        catch
        {
            _trayAvailable = false;
        }
    }

    private void OnHeadsetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceCardViewModel.BatteryPercent)
            or nameof(DeviceCardViewModel.IsConnected)
            or nameof(DeviceCardViewModel.BatteryText))
        {
            Dispatcher.UIThread.Post(UpdateTrayStatus);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedPreset) or nameof(MainViewModel.EqModeText))
            Dispatcher.UIThread.Post(UpdateTrayStatus);
    }

    private void UpdateTrayStatus()
    {
        if (_viewModel is null) return;

        string batteryText = _viewModel.Headset.BatteryText;
        if (_batteryMenuItem is not null)
            _batteryMenuItem.Header = $"电量：{batteryText}";

        var selected = _viewModel.SelectedPreset;
        if (_eqStatusMenuItem is not null)
            _eqStatusMenuItem.Header = $"当前 EQ：{selected?.Name ?? "自定义"}";

        foreach (var (preset, item) in _eqMenuItems)
            item.Header = (selected?.Id == preset.Id ? "✓ " : "") + preset.Name;

        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = string.Equals(batteryText, "--", StringComparison.Ordinal)
                ? "HECATE G2 · 未连接"
                : $"HECATE G2 · 电量 {batteryText}";
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.ShowInTaskbar = true;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting || !_trayAvailable) return;
        e.Cancel = true;
        _window?.Hide();
    }
}
