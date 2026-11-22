using CommunityToolkit.Mvvm.ComponentModel;

namespace PeripheralBattery.App.ViewModels;

public partial class EqBandViewModel : ObservableObject
{
    private readonly Action<EqBandViewModel>? _changed;
    private bool _suppressChanged;

    public EqBandViewModel(int index, string frequencyLabel, Action<EqBandViewModel>? changed = null)
    {
        Index = index;
        FrequencyLabel = frequencyLabel;
        _changed = changed;
    }

    public int Index { get; }
    public string FrequencyLabel { get; }

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private string _gainText = "0.0 dB";

    public void SetGain(double gainDb)
    {
        _suppressChanged = true;
        GainDb = Math.Clamp(Math.Round(gainDb * 2, MidpointRounding.AwayFromZero) / 2, -3, 3);
        GainText = FormatGain(GainDb);
        _suppressChanged = false;
    }

    partial void OnGainDbChanged(double value)
    {
        var snapped = Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, -3, 3);
        if (Math.Abs(value - snapped) > 0.001) GainDb = snapped;

        GainText = FormatGain(snapped);
        if (!_suppressChanged) _changed?.Invoke(this);
    }

    private static string FormatGain(double gainDb) =>
        $"{gainDb:+0.0;-0.0;0.0} dB";
}
