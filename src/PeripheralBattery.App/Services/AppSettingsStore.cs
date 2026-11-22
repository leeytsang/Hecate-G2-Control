using System.Text.Json;

namespace PeripheralBattery.App.Services;

public sealed class AppSettings
{
    public bool AutoSwitchAudio { get; set; } = true;
    public string SelectedEqPresetId { get; set; } = "neutral";
    public ulong? HeadsetBluetoothAddress { get; set; }
    public double[]? CustomEqGainsDb { get; set; }
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveGate = new();
    private readonly string _path;

    public AppSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HecateG2Control");
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        lock (_saveGate)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_saveGate)
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                tempPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tempPath, _path, overwrite: true);
                tempPath = null;
            }
            catch
            {
                // Settings are a convenience; a write failure must not break device control.
            }
            finally
            {
                if (tempPath is not null)
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
    }
}
