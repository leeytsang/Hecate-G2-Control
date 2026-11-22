namespace PeripheralBattery.Core.Audio;

public sealed record G2EqState
{
    public required int[] GainCodes { get; init; }

    public double[] GainsDb => GainCodes.Select(code => code * 0.5 - 3).ToArray();

    public static G2EqState Neutral => new() { GainCodes = [6, 6, 6, 6] };
}
