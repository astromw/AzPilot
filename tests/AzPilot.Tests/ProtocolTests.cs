using AzPilot.Core;
using Xunit;

namespace AzPilot.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(0, 3, "000000")]
    [InlineData(1, 3, "010000")]
    [InlineData(0x123456, 3, "563412")]
    [InlineData(65535, 2, "FFFF")]
    public void LittleEndianEncoding_MatchesWireFormat(long value, int bytes, string expected) =>
        Assert.Equal(expected, SynScanProtocol.EncodeLittleEndian(value, bytes));

    [Theory]
    [InlineData("000000", 0)]
    [InlineData("010000", 1)]
    [InlineData("563412", 0x123456)]
    [InlineData("FFFF", 65535)]
    public void LittleEndianDecoding_MatchesWireFormat(string payload, long expected) =>
        Assert.Equal(expected, SynScanProtocol.DecodeLittleEndian(payload));

    [Fact]
    public void PositionConversion_UsesSignedEncoderOffset()
    {
        const long counts = 902400;
        Assert.Equal(0, SynScanProtocol.StepsToDegrees(0x800000, counts), 8);
        Assert.Equal(90, SynScanProtocol.StepsToDegrees(0x800000 + counts / 4, counts), 8);
    }

    [Fact]
    public void RateConversion_ProducesPositiveBoundedPeriod()
    {
        var period = SynScanProtocol.RateToTimerPeriod(0.25, 902400, 4500000, 16);
        Assert.True(period > 6);
        Assert.Equal(6, SynScanProtocol.RateToTimerPeriod(4, 100000, 1, 1));
    }

    [Fact]
    public void ShortestDelta_CrossesNorthCorrectly()
    {
        Assert.Equal(20, Angle.ShortestDelta(350, 10), 8);
        Assert.Equal(-20, Angle.ShortestDelta(10, 350), 8);
    }
}
