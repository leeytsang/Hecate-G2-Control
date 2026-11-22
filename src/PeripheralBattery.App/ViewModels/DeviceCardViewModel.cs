using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeripheralBattery.App.ViewModels;

public partial class DeviceCardViewModel : ObservableObject
{
    private static readonly IImmutableBrush HeadsetBrush = new ImmutableSolidColorBrush(Color.Parse("#2DD4BF"));
    private static readonly IImmutableBrush LowBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IImmutableBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#556170"));
    private static readonly IImmutableBrush ConnectedBrush = new ImmutableSolidColorBrush(Color.Parse("#45D483"));
    private static readonly IImmutableBrush OfflineBrush = new ImmutableSolidColorBrush(Color.Parse("#596574"));

    public DeviceCardViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public string KindLabel => "无线耳机";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private int? _batteryPercent;

    [ObservableProperty]
    private string _connectionLabel = "正在检测";

    [ObservableProperty]
    private string _detail = "等待设备响应";

    [ObservableProperty]
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public bool HasBattery => BatteryPercent is >= 0 and <= 100;
    public bool HasNoBattery => !HasBattery;
    public double BatteryValue => BatteryPercent ?? 0;
    public string BatteryText => HasBattery ? $"{BatteryPercent}%" : "--";
    public string BatteryCaption => HasBattery ? "当前电量" : "电量不可用";
    public string UnavailableText => "暂未读取到电量";
    public IImmutableBrush AccentBrush => HeadsetBrush;
    public IImmutableBrush BatteryBrush => !HasBattery ? MutedBrush : BatteryPercent <= 20 ? LowBrush : AccentBrush;
    public IImmutableBrush ConnectionBrush => IsConnected ? ConnectedBrush : OfflineBrush;

    partial void OnBatteryPercentChanged(int? value)
    {
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(HasNoBattery));
        OnPropertyChanged(nameof(BatteryValue));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(BatteryCaption));
        OnPropertyChanged(nameof(BatteryBrush));
    }

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectionBrush));
}
