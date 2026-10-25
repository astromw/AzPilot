using System.Globalization;

namespace AzPilot.Core;

public static class Angle
{
    public static double Wrap360(double value) => ((value % 360) + 360) % 360;
    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
    public static double ShortestDelta(double from, double to) => ((to - from + 540) % 360) - 180;

    public static string Degrees(double value, bool signed = false)
    {
        var sign = value < 0 ? "−" : signed ? "+" : "";
        value = Math.Abs(WrapSignedForDisplay(value));
        var degrees = (int)value;
        var minutesFull = (value - degrees) * 60;
        var minutes = (int)minutesFull;
        var seconds = (minutesFull - minutes) * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{degrees:000}° {minutes:00}′ {seconds.ToString("00.0", CultureInfo.InvariantCulture)}″");
    }

    public static string Hours(double value)
    {
        value = ((value % 24) + 24) % 24;
        var hours = (int)value;
        var minutesFull = (value - hours) * 60;
        var minutes = (int)minutesFull;
        var seconds = (minutesFull - minutes) * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}ʰ {minutes:00}ᵐ {seconds.ToString("00.0", CultureInfo.InvariantCulture)}ˢ");
    }

    private static double WrapSignedForDisplay(double value) => value;
}
