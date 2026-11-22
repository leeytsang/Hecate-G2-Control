namespace PeripheralBattery.Core.Audio;

public sealed record EqPreset(string Id, string Name, string Description, double[] GainsDb);

public static class G2EqPresets
{
    public static readonly double[] G2Frequencies = [100, 500, 2000, 8000];
    private static readonly double[] TenBandFrequencies = [30, 60, 120, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static readonly EqPreset Neutral = new(
        "neutral",
        "默认",
        "平直曲线",
        [0, 0, 0, 0]);

    public static readonly EqPreset DeltaForce = FromOfficialCurve(
        "delta-force",
        "三角洲",
        "官方三角洲曲线 · G2 4 段近似",
        [-4, -4, -4, 2, -2, 4, 4, 2, 2, -4]);

    public static readonly EqPreset Pubg = FromOfficialCurve(
        "pubg",
        "PUBG",
        "官方 PUBG 曲线 · G2 4 段近似",
        [-4, -4, -3, -3, 2, 4, 4, 2, -2]);

    public static readonly EqPreset Valorant = FromOfficialCurve(
        "valorant",
        "无畏契约",
        "官方 VALORANT 曲线 · G2 4 段近似",
        [-3, -3, -3, -3, 0, 3, 3, 4, 1, 1]);

    public static readonly EqPreset Apex = FromOfficialCurve(
        "apex",
        "APEX",
        "官方 APEX 曲线 · G2 4 段近似",
        [-3, -3, 2, 4, 3, 3, 4, 4, 1, -2]);

    public static readonly EqPreset Cs2 = FromOfficialCurve(
        "cs2",
        "CS2",
        "官方 CS2 曲线 · G2 4 段近似",
        [-3, -3, -3, -3, 5, 5, 5, 3, -2, -2]);

    public static IReadOnlyList<EqPreset> BuiltIn { get; } =
        [Neutral, DeltaForce, Pubg, Valorant, Apex, Cs2];

    public static EqPreset? Find(string id) => BuiltIn.FirstOrDefault(p => p.Id == id);

    public static double[] MapTenBandToG2(IReadOnlyList<double> gains)
    {
        var result = new double[G2Frequencies.Length];
        for (var i = 0; i < G2Frequencies.Length; i++)
            result[i] = Math.Clamp(Interpolate(gains, G2Frequencies[i]), -3, 3);

        return result;
    }

    private static double Interpolate(IReadOnlyList<double> gains, double targetHz)
    {
        var count = Math.Min(gains.Count, TenBandFrequencies.Length);
        if (count == 0) return 0;
        if (count == 1 || targetHz <= TenBandFrequencies[0]) return gains[0];
        if (targetHz >= TenBandFrequencies[count - 1]) return gains[count - 1];

        for (var i = 1; i < count; i++)
        {
            if (targetHz > TenBandFrequencies[i]) continue;
            var leftHz = TenBandFrequencies[i - 1];
            var rightHz = TenBandFrequencies[i];
            var t = (targetHz - leftHz) / (rightHz - leftHz);
            return gains[i - 1] + (gains[i] - gains[i - 1]) * t;
        }

        return gains[count - 1];
    }

    private static EqPreset FromOfficialCurve(string id, string name, string description, double[] officialTenBand) =>
        new(id, name, description, MapTenBandToG2(officialTenBand));
}
