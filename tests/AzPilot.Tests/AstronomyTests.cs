using AzPilot.Core;
using Xunit;

namespace AzPilot.Tests;

public class AstronomyTests
{
    [Fact]
    public void EquatorialHorizontal_RoundTripPreservesCoordinates()
    {
        var site = new SiteInfo(52.1, -1.7, 100);
        var time = new DateTimeOffset(2026, 10, 24, 22, 15, 0, TimeSpan.Zero);
        var horizontal = Astronomy.EquatorialToHorizontal(5.75, -12.4, site, time);
        var equatorial = Astronomy.HorizontalToEquatorial(horizontal.Azimuth, horizontal.Altitude, site, time);
        Assert.Equal(5.75, equatorial.RightAscension, 8);
        Assert.Equal(-12.4, equatorial.Declination, 8);
    }

    [Fact]
    public void NorthHorizon_ConvertsToExpectedDeclination()
    {
        var site = new SiteInfo(40, 0);
        var eq = Astronomy.HorizontalToEquatorial(0, 0, site, DateTimeOffset.UtcNow);
        Assert.Equal(50, eq.Declination, 7);
    }

    [Fact]
    public void FixedEquatorialTarget_MovesInHorizontalCoordinates()
    {
        var site = SiteInfo.Default;
        var now = DateTimeOffset.UtcNow;
        var first = Astronomy.EquatorialToHorizontal(6, 20, site, now);
        var later = Astronomy.EquatorialToHorizontal(6, 20, site, now.AddMinutes(5));
        Assert.NotEqual(first.Azimuth, later.Azimuth);
        Assert.NotEqual(first.Altitude, later.Altitude);
    }
}
