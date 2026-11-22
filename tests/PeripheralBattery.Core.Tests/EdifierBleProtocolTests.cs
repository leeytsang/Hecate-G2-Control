using PeripheralBattery.Core.Audio;
using PeripheralBattery.Core.Protocols;

namespace PeripheralBattery.Core.Tests;

public sealed class EdifierBleProtocolTests
{
    [Fact]
    public void BatteryQuery_HasExpectedFrameAndChecksum()
    {
        var frame = EdifierBleProtocol.BuildBatteryQuery();
        Assert.Equal(new byte[] { 0xAA, 0xEC, 0xD0, 0x00, 0x00, 0x66 }, frame);
    }

    [Fact]
    public void ParsesRealHecateG2Response()
    {
        var response = new byte[] { 0xBB, 0xEC, 0xD0, 0x00, 0x01, 0x3D, 0xB5 };
        var ok = EdifierBleProtocol.TryParseBatteryNotification(response, out var percent);
        Assert.True(ok);
        Assert.Equal(61, percent);
    }

    [Fact]
    public void ScansPastNoiseAndOtherNotifications()
    {
        var response = new byte[] { 0x00, 0x0A, 0x13, 0xBB, 0xEC, 0xD0, 0x00, 0x01, 0x3D, 0xB5 };
        var ok = EdifierBleProtocol.TryParseBatteryNotification(response, out var percent);
        Assert.True(ok);
        Assert.Equal(61, percent);
    }

    [Fact]
    public void RejectsInvalidChecksum()
    {
        var response = new byte[] { 0xBB, 0xEC, 0xD0, 0x00, 0x01, 0x3D, 0x00 };
        Assert.False(EdifierBleProtocol.TryParseBatteryNotification(response, out _));
    }

    [Fact]
    public void BuildsG2EqWriteFrame()
    {
        var frame = EdifierBleProtocol.BuildEqWriteFrame(0, 7);
        Assert.Equal(new byte[] { 0xAA, 0xEC, 0x44, 0x00, 0x06, 0x00, 0x02, 0x00, 0x64, 0x07, 0x07, 0x54 }, frame);
    }

    [Fact]
    public void ParsesRealG2EqResponse()
    {
        var response = new byte[]
        {
            0xBB, 0xEC, 0x43, 0x00, 0x1A, 0x06, 0x04,
            0x00, 0x02, 0x00, 0x64, 0x06, 0x07,
            0x01, 0x02, 0x01, 0xF4, 0x06, 0x07,
            0x02, 0x02, 0x07, 0xD0, 0x06, 0x07,
            0x03, 0x02, 0x1F, 0x40, 0x06, 0x07,
            0xDF,
        };

        var ok = EdifierBleProtocol.TryParseEqResponse(response, out var codes);
        Assert.True(ok);
        Assert.Equal(new[] { 6, 6, 6, 6 }, codes);
    }

    [Theory]
    [InlineData(-3.0, 0)]
    [InlineData(-1.0, 4)]
    [InlineData(0.0, 6)]
    [InlineData(0.5, 7)]
    [InlineData(3.0, 12)]
    public void ConvertsGainDbToHardwareCode(double gainDb, int code)
    {
        Assert.Equal(code, EdifierBleProtocol.GainDbToCode(gainDb));
        Assert.Equal(gainDb, EdifierBleProtocol.GainCodeToDb(code), 1);
    }

    [Fact]
    public void MapsOfficialFpsCurvesToFourHardwareBands()
    {
        Assert.Equal(new double[] { -3, -2, 3, 2 }, G2EqPresets.DeltaForce.GainsDb);
        Assert.Equal(new double[] { -3, 2, 3, -2 }, G2EqPresets.Pubg.GainsDb);
        Assert.Equal(new double[] { -3, 0, 3, 1 }, G2EqPresets.Valorant.GainsDb);
    }
}
