using System.Globalization;
using System.Text;

namespace AzPilot.Core;

public static class SynScanProtocol
{
    public const double SiderealDegreesPerSecond = 15.04106864 / 3600.0;
    public static string Command(MountAxis axis, char command, string payload = "") => $":{command}{(int)axis}{payload}\r";

    public static string EncodeLittleEndian(long value, int bytes)
    {
        var builder = new StringBuilder(bytes * 2);
        for (var index = 0; index < bytes; index++)
            builder.Append(((value >> (index * 8)) & 0xff).ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public static long DecodeLittleEndian(string payload)
    {
        if (payload.Length == 0 || payload.Length % 2 != 0)
            throw new FormatException("A SynScan value must contain complete hexadecimal byte pairs.");
        long value = 0;
        for (var index = 0; index < payload.Length / 2; index++)
        {
            if (!byte.TryParse(payload.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                throw new FormatException($"Invalid SynScan hexadecimal value '{payload}'.");
            value |= (long)b << (index * 8);
        }
        return value;
    }

    public static double StepsToDegrees(long steps, long countsPerRevolution) =>
        (steps - 0x800000) * 360.0 / countsPerRevolution;

    public static long DegreesToSteps(double degrees, long countsPerRevolution) =>
        (long)Math.Round(degrees * countsPerRevolution / 360.0);

    public static long RateToTimerPeriod(double degreesPerSecond, long countsPerRevolution, long timerFrequency, int highSpeedRatio)
    {
        var effectiveRate = Math.Abs(degreesPerSecond);
        var highSpeed = effectiveRate > 128 * SiderealDegreesPerSecond;
        if (highSpeed) effectiveRate /= Math.Max(1, highSpeedRatio);
        var period = (long)Math.Round(timerFrequency * 360.0 / (countsPerRevolution * effectiveRate));
        return Math.Max(6, period);
    }
}
