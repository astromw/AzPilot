namespace AzPilot.Core;

/// <summary>Topocentric apparent-enough transforms for mount control (no refraction or precession).</summary>
public static class Astronomy
{
    private const double Rad = Math.PI / 180.0;
    private const double Deg = 180.0 / Math.PI;

    public static double SiderealTimeHours(DateTimeOffset utc, double longitudeDegrees)
    {
        var julianDate = utc.UtcDateTime.ToOADate() + 2415018.5;
        var centuries = (julianDate - 2451545.0) / 36525.0;
        var gmst = 280.46061837 + 360.98564736629 * (julianDate - 2451545.0)
                   + 0.000387933 * centuries * centuries - centuries * centuries * centuries / 38710000.0;
        return Angle.Wrap360(gmst + longitudeDegrees) / 15.0;
    }

    public static (double Altitude, double Azimuth) EquatorialToHorizontal(
        double rightAscensionHours, double declinationDegrees, SiteInfo site, DateTimeOffset utc)
    {
        var hourAngle = (SiderealTimeHours(utc, site.Longitude) - rightAscensionHours) * 15.0 * Rad;
        var dec = declinationDegrees * Rad;
        var lat = site.Latitude * Rad;
        var altitude = Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hourAngle));
        var azimuth = Math.Atan2(
            -Math.Cos(dec) * Math.Sin(hourAngle),
            Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(hourAngle));
        return (altitude * Deg, Angle.Wrap360(azimuth * Deg));
    }

    public static (double RightAscension, double Declination) HorizontalToEquatorial(
        double azimuthDegrees, double altitudeDegrees, SiteInfo site, DateTimeOffset utc)
    {
        var az = azimuthDegrees * Rad;
        var alt = altitudeDegrees * Rad;
        var lat = site.Latitude * Rad;
        var dec = Math.Asin(Math.Sin(lat) * Math.Sin(alt) + Math.Cos(lat) * Math.Cos(alt) * Math.Cos(az));
        var hourAngle = Math.Atan2(
            -Math.Cos(alt) * Math.Sin(az),
            Math.Sin(alt) * Math.Cos(lat) - Math.Cos(alt) * Math.Sin(lat) * Math.Cos(az));
        var ra = SiderealTimeHours(utc, site.Longitude) - hourAngle * Deg / 15.0;
        return (((ra % 24) + 24) % 24, dec * Deg);
    }
}
