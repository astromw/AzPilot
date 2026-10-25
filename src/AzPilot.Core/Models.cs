namespace AzPilot.Core;

public enum MountAxis { Azimuth = 1, Altitude = 2 }
public enum ConnectionKind { Simulator, Hardware }
public enum MotionKind { Stopped, Manual, GoTo, Tracking, Park, PulseGuide }
public enum TrackingRateMode { Sidereal = 0, Lunar = 1, Solar = 2 }
public enum AlignmentMode { SinglePoint, MultiPoint }
public enum GuideDirection { Up = 0, Down = 1, Right = 2, Left = 3 }
public enum MountProfile { AzEq6, AzEq5, Eq6R, SynScanDobsonian, GenericSynScanAltAz }

public sealed record MountProfileOption(MountProfile Profile, string Name, double MaximumSlewRate)
{
    public override string ToString() => Name;
}

public static class MountCatalog
{
    public const double ApplicationMaximumSlewRate = 4.0;
    public static IReadOnlyList<MountProfileOption> Profiles { get; } =
    [
        new(MountProfile.AzEq6, "Sky-Watcher AZ-EQ6", ApplicationMaximumSlewRate),
        new(MountProfile.AzEq5, "Sky-Watcher AZ-EQ5", ApplicationMaximumSlewRate),
        new(MountProfile.Eq6R, "Sky-Watcher EQ6-R (AZ mode)", ApplicationMaximumSlewRate),
        new(MountProfile.SynScanDobsonian, "Sky-Watcher SynScan GoTo Dobsonian", ApplicationMaximumSlewRate),
        new(MountProfile.GenericSynScanAltAz, "Generic SynScan Alt/Az", ApplicationMaximumSlewRate)
    ];
    public static MountProfileOption Get(MountProfile profile) => Profiles.FirstOrDefault(p => p.Profile == profile) ?? Profiles[0];
}

public sealed record BackendAxisState(double PositionDegrees, bool Moving, bool GoTo, bool HighSpeed);
public sealed record BackendState(BackendAxisState Azimuth, BackendAxisState Altitude)
{
    public bool IsMoving => Azimuth.Moving || Altitude.Moving;
}

public sealed record SiteInfo(double Latitude, double Longitude, double Elevation = 0)
{
    public static SiteInfo Default => new(51.4779, 0.0, 40);
}

public sealed record AppSettings
{
    public ConnectionKind ConnectionKind { get; init; } = ConnectionKind.Simulator;
    public MountProfile Mount { get; init; } = MountProfile.AzEq6;
    public string PortName { get; init; } = "COM3";
    public int BaudRate { get; init; } = 115200;
    public bool AutoReconnect { get; init; } = true;
    public double MinimumAltitude { get; init; } = 0;
    public double MaximumManualRate { get; init; } = 1.0;
    public double SelectedRate { get; init; } = 0.25;
    public double GoToRate { get; init; } = 0.5;
    public TrackingRateMode TrackingRate { get; init; } = TrackingRateMode.Sidereal;
    public AlignmentMode AlignmentMode { get; init; } = AlignmentMode.SinglePoint;
    public double GuideRateAzimuth { get; init; } = 0.002089074;
    public double GuideRateAltitude { get; init; } = 0.002089074;
    public bool HardwareMovementConfirmed { get; init; }
    public bool NightVision { get; init; }
    public int AlpacaPort { get; init; } = 11111;
    public bool AlpacaEnabled { get; init; } = true;
    public SiteInfo Site { get; init; } = SiteInfo.Default;
}

public sealed record MountSnapshot(
    bool Connected,
    bool Connecting,
    double Azimuth,
    double Altitude,
    double MechanicalAzimuth,
    double MechanicalAltitude,
    double RightAscension,
    double Declination,
    bool Slewing,
    bool Tracking,
    TrackingRateMode TrackingRate,
    AlignmentMode AlignmentMode,
    int AlignmentPointCount,
    bool AlignmentModelActive,
    double? AlignmentResidualDegrees,
    IReadOnlyList<AlignmentPoint> AlignmentPoints,
    bool IsPulseGuiding,
    bool AtPark,
    bool ParkInProgress,
    MotionKind Motion,
    string ConnectionText,
    string Firmware,
    string StatusMessage,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record GoToTarget(double Azimuth, double Altitude)
{
    public void Validate(double minimumAltitude)
    {
        if (!double.IsFinite(Azimuth) || Azimuth is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(Azimuth), "Azimuth must be from 0 up to (but not including) 360 degrees.");
        if (!double.IsFinite(Altitude) || Altitude < minimumAltitude || Altitude > 90)
            throw new ArgumentOutOfRangeException(nameof(Altitude), $"Altitude must be between {minimumAltitude:0.##}° and 90°.");
    }
}

public interface IMountBackend : IAsyncDisposable
{
    bool IsConnected { get; }
    string Description { get; }
    string Firmware { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<BackendState> ReadStateAsync(CancellationToken cancellationToken = default);
    Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default);
    Task SlewToAsync(MountAxis axis, double targetDegrees, bool slow, CancellationToken cancellationToken = default);
    Task StopAxisAsync(MountAxis axis, bool immediate = true, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}

public class MountException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class MountParkedException() : MountException("The mount is parked. Unpark before commanding motion.");
public sealed class MountInvalidOperationException(string message) : MountException(message);
