using AzPilot.Core;
using Xunit;

namespace AzPilot.Tests;

public class AlignmentTests
{
    [Fact]
    public void MultiPointModel_RequiresThreePointsAndFitsKnown3DTransformation()
    {
        var rotation = Normalize(new[] { 0.98, 0.03, -0.04, 0.02 });
        var mechanical = new[]
        {
            (Az: 0.0, Alt: 0.0), (Az: 90.0, Alt: 20.0), (Az: 180.0, Alt: 40.0),
            (Az: 270.0, Alt: 10.0), (Az: 45.0, Alt: 60.0), (Az: 225.0, Alt: -10.0)
        };
        var expected = mechanical.Select(point => Rotate(rotation, point.Az, point.Alt)).ToArray();
        var model = MultiPointAlignment.Empty;
        model = model.AddPoint(new(mechanical[0].Az, mechanical[0].Alt, expected[0].Az, expected[0].Alt));
        model = model.AddPoint(new(mechanical[1].Az, mechanical[1].Alt, expected[1].Az, expected[1].Alt));
        Assert.Equal(2, model.PointCount);
        Assert.False(model.IsActive);
        model = model.AddPoint(new(mechanical[2].Az, mechanical[2].Alt, expected[2].Az, expected[2].Alt));
        Assert.True(model.IsActive);
        Assert.True(model.ResidualDegrees < 0.00001);

        for (var i = 3; i < mechanical.Length; i++)
        {
            var sky = model.ToSky(mechanical[i].Az, mechanical[i].Alt);
            Assert.True(AngularError(sky, expected[i]) < 0.0001,
                $"Point {i}: predicted {sky}, expected {expected[i]}, RMS {model.ResidualDegrees}.");
            var roundTrip = model.ToMechanical(sky.Azimuth, sky.Altitude);
            Assert.True(AngularError(roundTrip, mechanical[i]) < 0.0001);
        }
    }

    [Fact]
    public void MultiPointModel_RefinesNearbyPointInsteadOfDuplicatingIt()
    {
        var model = MultiPointAlignment.Empty
            .AddPoint(new(0, 0, 1, 1))
            .AddPoint(new(90, 20, 91, 21))
            .AddPoint(new(180, 40, 181, 41));
        Assert.Equal(3, model.PointCount);
        model = model.AddPoint(new(0.05, 0.05, 1.02, 1.02));
        Assert.Equal(3, model.PointCount);
        Assert.Equal(1.02, model.Points[^1].SkyAzimuth, 6);
    }

    [Fact]
    public void MultiPointModel_WaitsForUsefulSeparation()
    {
        var model = MultiPointAlignment.Empty
            .AddPoint(new(0, 0, 0, 0))
            .AddPoint(new(1, 0, 1, 0))
            .AddPoint(new(2, 0, 2, 0));
        Assert.Equal(3, model.PointCount);
        Assert.False(model.IsActive);
    }

    [Fact]
    public async Task MultiPointSyncAndClear_WorkThroughEngineWithoutMovingMount()
    {
        await using var engine = new MountEngine(new AppSettings { AlignmentMode = AlignmentMode.MultiPoint, MaximumManualRate = 4 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(10, 20);
        Assert.Equal(1, engine.Snapshot.AlignmentPointCount);
        Assert.Equal(AlignmentMode.MultiPoint, engine.Snapshot.AlignmentMode);
        Assert.False(engine.Snapshot.AlignmentModelActive);
        var point = Assert.Single(engine.Snapshot.AlignmentPoints);
        Assert.Equal(0, point.MechanicalAzimuth, 4);
        Assert.Equal(0, point.MechanicalAltitude, 4);
        Assert.Equal(10, point.SkyAzimuth, 4);
        Assert.Equal(20, point.SkyAltitude, 4);
        Assert.Equal(10, engine.Snapshot.Azimuth, 4);
        Assert.Equal(20, engine.Snapshot.Altitude, 4);
        await engine.MoveAxisAsync(MountAxis.Azimuth, 4);
        await Task.Delay(2600);
        await engine.StopAxisAsync(MountAxis.Azimuth);
        await engine.SyncToAltAzAsync(20, 20);
        await engine.MoveAxisAsync(MountAxis.Azimuth, 4);
        await Task.Delay(2600);
        await engine.StopAxisAsync(MountAxis.Azimuth);
        await engine.SyncToAltAzAsync(30, 20);
        Assert.Equal(3, engine.Snapshot.AlignmentPointCount);
        Assert.True(engine.Snapshot.AlignmentModelActive);
        Assert.Equal(3, engine.Snapshot.AlignmentPoints.Count);
        await engine.ClearAlignmentPointsAsync();
        Assert.Equal(0, engine.Snapshot.AlignmentPointCount);
        Assert.Empty(engine.Snapshot.AlignmentPoints);
        Assert.Equal(engine.Snapshot.MechanicalAzimuth, engine.Snapshot.Azimuth, 4);
        Assert.Equal(engine.Snapshot.MechanicalAltitude, engine.Snapshot.Altitude, 4);
    }

    [Fact]
    public async Task SinglePointSync_ReportsOnePointAndClearResetsCount()
    {
        await using var engine = new MountEngine(new AppSettings { AlignmentMode = AlignmentMode.SinglePoint });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        Assert.Equal(0, engine.Snapshot.AlignmentPointCount);
        await engine.SyncToAltAzAsync(10, 20);
        Assert.Equal(1, engine.Snapshot.AlignmentPointCount);
        var point = Assert.Single(engine.Snapshot.AlignmentPoints);
        Assert.Equal(0, point.MechanicalAzimuth, 4);
        Assert.Equal(0, point.MechanicalAltitude, 4);
        Assert.Equal(10, point.SkyAzimuth, 4);
        Assert.Equal(20, point.SkyAltitude, 4);
        Assert.Equal(engine.Snapshot.RightAscension, point.RightAscension, 4);
        Assert.Equal(engine.Snapshot.Declination, point.Declination, 4);
        Assert.Equal(10, engine.Snapshot.Azimuth, 4);
        await engine.SyncToAltAzAsync(12, 22);
        Assert.Equal(1, engine.Snapshot.AlignmentPointCount);
        await engine.ClearAlignmentPointsAsync();
        Assert.Equal(0, engine.Snapshot.AlignmentPointCount);
        Assert.Equal(engine.Snapshot.MechanicalAzimuth, engine.Snapshot.Azimuth, 4);
        Assert.Equal(engine.Snapshot.MechanicalAltitude, engine.Snapshot.Altitude, 4);
    }

    [Theory]
    [InlineData(AlignmentMode.SinglePoint)]
    [InlineData(AlignmentMode.MultiPoint)]
    public async Task EquatorialSync_StoresEnteredCelestialCoordinatesAtSyncTime(AlignmentMode mode)
    {
        await using var engine = new MountEngine(new AppSettings { AlignmentMode = mode });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToCoordinatesAsync(12.345678, -20.25);

        var point = Assert.Single(engine.Snapshot.AlignmentPoints);
        Assert.Equal(12.345678, point.RightAscension, 9);
        Assert.Equal(-20.25, point.Declination, 9);
        var storedRa = point.RightAscension;
        var storedDec = point.Declination;
        await Task.Delay(1100);
        point = Assert.Single(engine.Snapshot.AlignmentPoints);
        Assert.Equal(storedRa, point.RightAscension, 12);
        Assert.Equal(storedDec, point.Declination, 12);
    }

    private static double AngularError(
        (double Az, double Alt) a, (double Az, double Alt) b)
    {
        var av = Vector(a.Az, a.Alt);
        var bv = Vector(b.Az, b.Alt);
        return Math.Acos(Math.Clamp(av.X * bv.X + av.Y * bv.Y + av.Z * bv.Z, -1, 1)) * 180 / Math.PI;
    }

    private static (double Az, double Alt) Rotate(double[] q, double azimuth, double altitude)
    {
        var v = Vector(azimuth, altitude);
        // t = 2 * q.xyz cross v
        var t = (
            X: 2 * (q[2] * v.Z - q[3] * v.Y),
            Y: 2 * (q[3] * v.X - q[1] * v.Z),
            Z: 2 * (q[1] * v.Y - q[2] * v.X));
        // q.xyz cross t
        var u = (
            X: q[2] * t.Z - q[3] * t.Y,
            Y: q[3] * t.X - q[1] * t.Z,
            Z: q[1] * t.Y - q[2] * t.X);
        var rotated = (X: v.X + q[0] * t.X + u.X, Y: v.Y + q[0] * t.Y + u.Y, Z: v.Z + q[0] * t.Z + u.Z);
        return (Az: Angle.Wrap360(Math.Atan2(rotated.X, rotated.Y) * 180 / Math.PI),
            Alt: Math.Asin(Math.Clamp(rotated.Z, -1, 1)) * 180 / Math.PI);
    }

    private static (double X, double Y, double Z) Vector(double azimuth, double altitude)
    {
        var az = azimuth * Math.PI / 180;
        var alt = altitude * Math.PI / 180;
        return (Math.Cos(alt) * Math.Sin(az), Math.Cos(alt) * Math.Cos(az), Math.Sin(alt));
    }

    private static double[] Normalize(double[] values)
    {
        var length = Math.Sqrt(values.Sum(value => value * value));
        return values.Select(value => value / length).ToArray();
    }
}
