using AzPilot.Core;
using Xunit;

namespace AzPilot.Tests;

public class EngineTests
{
    private static MountEngine Create(double minimumAltitude = 0) => new(new AppSettings { MinimumAltitude = minimumAltitude, MaximumManualRate = 2 });

    [Fact]
    public async Task ManualSlewAndStop_ChangesPositionThenStops()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.MoveAxisAsync(MountAxis.Altitude, 1);
        await Task.Delay(650);
        await engine.StopAllAsync();
        var snapshot = engine.Snapshot;
        Assert.True(snapshot.Altitude > 0.3);
        await Task.Delay(300);
        Assert.False(engine.Snapshot.Slewing);
        Assert.Equal(snapshot.Altitude, engine.Snapshot.Altitude, 1);
    }

    [Fact]
    public async Task AltAzGoTo_ReachesRequestedPosition()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SlewToAltAzAsync(2, 2);
        await WaitUntil(() => !engine.Snapshot.Slewing, 6000);
        Assert.Equal(2, engine.Snapshot.Azimuth, 1);
        Assert.Equal(2, engine.Snapshot.Altitude, 1);
    }

    [Fact]
    public async Task MinimumAltitude_RejectsUnsafeGoToAndManualMotion()
    {
        await using var engine = Create(10);
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SlewToAltAzAsync(0, 5));
        await engine.SyncToAltAzAsync(0, 10);
        await Assert.ThrowsAnyAsync<MountException>(() => engine.MoveAxisAsync(MountAxis.Altitude, -0.1));
    }

    [Fact]
    public async Task Sync_ChangesPointingWithoutMovingEncoders()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        var before = engine.Snapshot;
        await engine.SyncToAltAzAsync(90, 30);
        var after = engine.Snapshot;
        Assert.Equal(before.MechanicalAzimuth, after.MechanicalAzimuth, 5);
        Assert.Equal(before.MechanicalAltitude, after.MechanicalAltitude, 5);
        Assert.Equal(90, after.Azimuth, 5);
        Assert.Equal(30, after.Altitude, 5);
    }

    [Fact]
    public async Task SyncWhileTracking_UpdatesPointingAndKeepsTracking()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        await engine.SyncToAltAzAsync(190, 50);
        var synced = engine.Snapshot;
        Assert.True(synced.Tracking);
        Assert.False(synced.Slewing);
        Assert.Equal(190, synced.Azimuth, 4);
        Assert.Equal(50, synced.Altitude, 4);
        Assert.Contains("tracking continues", synced.StatusMessage);
        await Task.Delay(1400);
        var current = engine.Snapshot;
        Assert.True(current.Tracking);
        Assert.True(Math.Abs(synced.RightAscension - current.RightAscension) < 0.0002);
        Assert.True(Math.Abs(synced.Declination - current.Declination) < 0.001);
    }

    [Fact]
    public async Task Tracking_HoldsSkyCoordinates()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        var initial = engine.Snapshot;
        await engine.SetTrackingAsync(true);
        await Task.Delay(2400);
        var current = engine.Snapshot;
        if (!current.Tracking) throw new Exception(string.Join("\n", engine.RecentLogs));
        Assert.True(current.Tracking);
        Assert.Equal(initial.RightAscension, current.RightAscension, 2);
        Assert.Equal(initial.Declination, current.Declination, 2);
        await engine.StopAllAsync();
        Assert.False(engine.Snapshot.Tracking);
    }

    [Theory]
    [InlineData(180, 45)]
    [InlineData(90, 20)]
    [InlineData(270, 60)]
    public async Task SiderealTracking_LongRunHasTightEquatorialDrift(double azimuth, double altitude)
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(azimuth, altitude);
        await engine.SetTrackingAsync(true);
        var initial = engine.Snapshot;
        await Task.Delay(6500);
        var current = engine.Snapshot;
        Assert.True(current.Tracking, string.Join("\n", engine.RecentLogs));
        var raDriftHours = Math.Abs((current.RightAscension - initial.RightAscension + 36) % 24 - 12);
        var decDriftDegrees = Math.Abs(current.Declination - initial.Declination);
        Assert.True(raDriftHours < 0.0001, $"RA drift was {raDriftHours:0.00000000} h at Az {azimuth}, Alt {altitude}.");
        Assert.True(decDriftDegrees < 0.001, $"Dec drift was {decDriftDegrees:0.000000}° at Az {azimuth}, Alt {altitude}.");
    }

    [Fact]
    public async Task NegativeMinimumAltitude_AllowsBelowHorizonGoToAndEnforcesLimit()
    {
        await using var engine = Create(-5);
        engine.UpdateSettings(engine.Settings with { GoToRate = 2 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SlewToAltAzAsync(0, -4);
        await WaitUntil(() => !engine.Snapshot.Slewing, 6000);
        Assert.Equal(-4, engine.Snapshot.Altitude, 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.SlewToAltAzAsync(20, -5.1));
        engine.UpdateSettings(engine.Settings with { MinimumAltitude = -90 });
        await engine.SlewToAltAzAsync(0, -10);
        await WaitUntil(() => !engine.Snapshot.Slewing, 8000);
        Assert.Equal(-10, engine.Snapshot.Altitude, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.UpdateSettings(engine.Settings with { MinimumAltitude = -90.1 }));
    }

    [Fact]
    public async Task NegativeMinimumAltitude_AllowsManualMotionAndStopsAtConfiguredBoundary()
    {
        await using var engine = Create(-5);
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(0, -2);
        await engine.MoveAxisAsync(MountAxis.Altitude, -0.5);
        await Task.Delay(500);
        Assert.True(engine.Snapshot.Slewing);
        Assert.True(engine.Snapshot.Altitude < -2.1);
        await engine.StopAllAsync();

        await engine.SyncToAltAzAsync(0, -4.85);
        await engine.MoveAxisAsync(MountAxis.Altitude, -1);
        await WaitUntil(() => !engine.Snapshot.Slewing, 4000);
        Assert.False(engine.Snapshot.Tracking);
        Assert.True(engine.Snapshot.Altitude >= -5.3, $"Altitude was {engine.Snapshot.Altitude}.");
        Assert.Contains("Altitude limit", engine.Snapshot.StatusMessage);
    }

    [Fact]
    public async Task ManualRecoveryFromBelowMinimum_AllowsMotionTowardSafety()
    {
        await using var engine = Create(10);
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        Assert.True(engine.Snapshot.Altitude < engine.Settings.MinimumAltitude);

        await engine.MoveAxisAsync(MountAxis.Altitude, 0.5);
        await Task.Delay(750);

        var recovering = engine.Snapshot;
        Assert.True(recovering.Slewing, recovering.StatusMessage);
        Assert.True(recovering.Altitude > 0.2, $"Altitude was {recovering.Altitude}.");
        Assert.DoesNotContain("Altitude limit", recovering.StatusMessage);
        await Assert.ThrowsAsync<MountInvalidOperationException>(() =>
            engine.MoveAxisAsync(MountAxis.Altitude, -0.5));
        await engine.StopAllAsync();
    }

    [Fact]
    public async Task ManualRecoveryFromAboveNinety_AllowsMotionTowardSafety()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(0, 89.9);
        await engine.MoveAxisAsync(MountAxis.Altitude, 2);
        await WaitUntil(() => engine.Snapshot.Altitude > 90 && !engine.Snapshot.Slewing, 4000);
        var outside = engine.Snapshot.Altitude;

        await engine.MoveAxisAsync(MountAxis.Altitude, -0.5);
        await Task.Delay(750);

        var recovering = engine.Snapshot;
        Assert.True(recovering.Slewing, recovering.StatusMessage);
        Assert.True(recovering.Altitude < outside - 0.2,
            $"Altitude was {recovering.Altitude}, previously {outside}.");
        await engine.StopAllAsync();
    }

    [Fact]
    public async Task ManualRelease_ResumesTrackingAtCurrentCoordinates()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        await engine.MoveAxisAsync(MountAxis.Azimuth, 0.25);
        Assert.False(engine.Snapshot.Tracking);
        Assert.True(engine.Snapshot.Slewing);
        await Task.Delay(500);
        await engine.StopAxisAsync(MountAxis.Azimuth);
        Assert.True(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
        var resumed = engine.Snapshot;
        await Task.Delay(1300);
        var current = engine.Snapshot;
        Assert.True(Math.Abs(resumed.RightAscension - current.RightAscension) < 0.0002);
        Assert.True(Math.Abs(resumed.Declination - current.Declination) < 0.001);
    }

    [Fact]
    public async Task GoToCompletion_ResumesTrackingAtDestination()
    {
        await using var engine = Create();
        engine.UpdateSettings(engine.Settings with { GoToRate = 2 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        await engine.SlewToAltAzAsync(181, 46);
        Assert.False(engine.Snapshot.Tracking);
        await WaitUntil(() => engine.Snapshot.Tracking && !engine.Snapshot.Slewing, 6000);
        Assert.Equal(181, engine.Snapshot.Azimuth, 1);
        Assert.Equal(46, engine.Snapshot.Altitude, 1);
        var resumed = engine.Snapshot;
        await Task.Delay(1200);
        Assert.True(Math.Abs(resumed.RightAscension - engine.Snapshot.RightAscension) < 0.0002);
        Assert.True(Math.Abs(resumed.Declination - engine.Snapshot.Declination) < 0.001);
    }

    [Fact]
    public async Task StopDuringGoTo_CancelsPendingTrackingResume()
    {
        await using var engine = Create();
        engine.UpdateSettings(engine.Settings with { GoToRate = 2 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        await engine.SlewToAltAzAsync(200, 50);
        await engine.StopAllAsync();
        await Task.Delay(700);
        Assert.False(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
    }

    [Fact]
    public async Task CancelGoTo_ResumesPreviousTrackingRate()
    {
        await using var engine = Create();
        engine.UpdateSettings(engine.Settings with { GoToRate = 2 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingRateAsync(TrackingRateMode.Solar);
        await engine.SetTrackingAsync(true);
        await engine.SlewToAltAzAsync(200, 50);
        await engine.CancelGoToAsync();
        Assert.True(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
        Assert.Equal(TrackingRateMode.Solar, engine.Snapshot.TrackingRate);
        Assert.Contains("GoTo cancelled", engine.Snapshot.StatusMessage);
        await Task.Delay(700);
        Assert.True(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
    }

    [Fact]
    public async Task CancelGoTo_WithoutPreviousTrackingLeavesTrackingOff()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SlewToAltAzAsync(2, 2);
        await engine.CancelGoToAsync();
        Assert.False(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
        Assert.Contains("tracking remains off", engine.Snapshot.StatusMessage);
    }

    [Fact]
    public async Task IntentionalDisconnect_PreservesTrackingAndLastMotionState()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        var before = engine.Snapshot;
        await engine.DisconnectAsync();
        var after = engine.Snapshot;
        Assert.False(after.Connected);
        Assert.True(after.Tracking);
        Assert.Equal(before.TrackingRate, after.TrackingRate);
        Assert.Equal(MotionKind.Tracking, after.Motion);
        Assert.Contains("state preserved", after.StatusMessage);
        Assert.Contains(engine.RecentLogs, line => line.Contains("without stopping the mount"));
    }

    [Fact]
    public async Task UnsafeTrackingStart_LeavesTrackingOffAndMountStopped()
    {
        await using var engine = Create(10);
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 9);
        await Assert.ThrowsAsync<MountInvalidOperationException>(() => engine.SetTrackingAsync(true));
        Assert.False(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
        Assert.False(engine.Snapshot.IsPulseGuiding);
        Assert.Contains("rejected", engine.Snapshot.StatusMessage);
    }

    [Fact]
    public async Task LunarAndSolarTrackingRates_CanChangeWithoutStoppingTracking()
    {
        await using var engine = Create();
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        var initialRa = engine.Snapshot.RightAscension;
        await engine.SetTrackingRateAsync(TrackingRateMode.Lunar);
        Assert.True(engine.Snapshot.Tracking);
        Assert.Equal(TrackingRateMode.Lunar, engine.Snapshot.TrackingRate);
        await Task.Delay(3200);
        var lunarRa = engine.Snapshot.RightAscension;
        var lunarRaMotion = (lunarRa - initialRa + 36) % 24 - 12;
        Assert.InRange(lunarRaMotion, 0.000015, 0.000050);
        await engine.SetTrackingRateAsync(TrackingRateMode.Solar);
        Assert.True(engine.Snapshot.Tracking);
        Assert.Equal(TrackingRateMode.Solar, engine.Snapshot.TrackingRate);
        await engine.SetTrackingAsync(false);
        Assert.False(engine.Snapshot.Tracking);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs) await Task.Delay(100);
        Assert.True(condition(), "Condition was not reached before timeout.");
    }
}

public class ParkAndPulseTests
{
    [Fact]
    public async Task ParkReturnsToSavedPositionAndLocksMotionUntilUnpark()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.SlewToAltAzAsync(2, 3);
        await WaitFor(engine, s => !s.Slewing);
        await engine.SetParkAsync();
        await engine.SlewToAltAzAsync(5, 6);
        await WaitFor(engine, s => !s.Slewing);
        await engine.ParkAsync();
        var parked = await WaitFor(engine, s => s.AtPark);
        Assert.Equal(2, parked.Azimuth, 2);
        Assert.Equal(3, parked.Altitude, 2);
        await Assert.ThrowsAsync<MountParkedException>(() => engine.MoveAxisAsync(MountAxis.Azimuth, 0.1));
        await Assert.ThrowsAsync<MountParkedException>(() => engine.SetTrackingAsync(true));
        await Assert.ThrowsAsync<MountParkedException>(() => engine.SyncToAltAzAsync(2, 3));
        await Assert.ThrowsAsync<MountParkedException>(() => engine.PulseGuideAsync(GuideDirection.Up, 100));
        await engine.UnparkAsync();
        Assert.False(engine.Snapshot.AtPark);
        await engine.MoveAxisAsync(MountAxis.Azimuth, 0.1);
        Assert.True(engine.Snapshot.Slewing);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ParkFromBelowMinimum_CanMoveTowardSafeParkPosition()
    {
        await using var engine = new MountEngine(new AppSettings { MinimumAltitude = 10, GoToRate = 2 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SlewToAltAzAsync(0, 10);
        await WaitFor(engine, s => !s.Slewing);
        await engine.SetParkAsync();

        engine.UpdateSettings(engine.Settings with { MinimumAltitude = -90 });
        await engine.SlewToAltAzAsync(0, 5);
        await WaitFor(engine, s => !s.Slewing);
        engine.UpdateSettings(engine.Settings with { MinimumAltitude = 10 });
        Assert.True(engine.Snapshot.Altitude < engine.Settings.MinimumAltitude);

        await engine.ParkAsync();
        var parked = await WaitFor(engine, s => s.AtPark);
        Assert.Equal(10, parked.Altitude, 2);
        Assert.False(parked.ParkInProgress);
    }

    [Fact]
    public async Task AbortParkDoesNotLaterReportParked()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.SetParkAsync();
        await engine.SlewToAltAzAsync(2, 3);
        await WaitFor(engine, s => !s.Slewing);
        await engine.ParkAsync();
        await engine.StopAllAsync();
        await Task.Delay(300);
        Assert.False(engine.Snapshot.AtPark);
        Assert.False(engine.Snapshot.ParkInProgress);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PulseGuideUpChangesOnlyAltitudeAndResumesTracking()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        var before = engine.Snapshot;
        await engine.PulseGuideAsync(GuideDirection.Up, 1000);
        Assert.True(engine.Snapshot.IsPulseGuiding);
        Assert.True(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
        Assert.Equal(MotionKind.PulseGuide, engine.Snapshot.Motion);
        await Assert.ThrowsAsync<MountInvalidOperationException>(() => engine.MoveAxisAsync(MountAxis.Azimuth, 0.1));
        await WaitFor(engine, s => !s.IsPulseGuiding);
        var after = engine.Snapshot;
        Assert.True(after.Tracking);
        Assert.False(after.Slewing);
        Assert.True(after.Altitude > before.Altitude + 0.0001);
        Assert.Contains(engine.RecentLogs, line => line.Contains("Pulse guide Up") && line.Contains("AZ correction 0.000000") && line.Contains("Alt correction 0.002089"));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PulseGuideLeftChangesOnlyAzimuth()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        var before = engine.Snapshot;
        await engine.PulseGuideAsync(GuideDirection.Left, 2000);
        await WaitFor(engine, s => !s.IsPulseGuiding);
        Assert.Contains(engine.RecentLogs, line => line.Contains("Pulse guide Left") && line.Contains("AZ correction -0.004178") && line.Contains("Alt correction 0.000000"));
        Assert.True(Math.Abs(engine.Snapshot.Altitude - before.Altitude) < 0.02);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task StopCancelsPulseAndDoesNotResumeTracking()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.SetTrackingAsync(true);
        await engine.PulseGuideAsync(GuideDirection.Left, 1500);
        await engine.StopAllAsync();
        await Task.Delay(1800);
        Assert.False(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.IsPulseGuiding);
        Assert.False(engine.Snapshot.Slewing);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PulseRequiresTrackingAndValidSettings()
    {
        var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await Assert.ThrowsAsync<MountInvalidOperationException>(() => engine.PulseGuideAsync(GuideDirection.Up, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.PulseGuideAsync(GuideDirection.Up, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetGuideRates(0, 0.002));
        await engine.DisposeAsync();
    }

    private static async Task<MountSnapshot> WaitFor(MountEngine engine, Func<MountSnapshot, bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition(engine.Snapshot)) return engine.Snapshot;
            await Task.Delay(100);
        }
        Assert.Fail("Condition was not reached before timeout.");
        return engine.Snapshot;
    }

}

public class MountProfileAndSpeedTests
{
    [Fact]
    public void CatalogIncludesRequestedMountsWithFourDegreeApplicationCeiling()
    {
        Assert.Contains(MountCatalog.Profiles, p => p.Profile == MountProfile.AzEq5);
        Assert.Contains(MountCatalog.Profiles, p => p.Profile == MountProfile.SynScanDobsonian);
        Assert.Contains(MountCatalog.Profiles, p => p.Profile == MountProfile.Eq6R);
        Assert.All(MountCatalog.Profiles, p => Assert.Equal(4.0, p.MaximumSlewRate));
    }

    [Fact]
    public void MountProfileAndMaximumRateSurviveSettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"azpilot-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Mount = MountProfile.SynScanDobsonian, SelectedRate = 4.0, MaximumManualRate = 4.0 });
            var loaded = store.Load();
            Assert.Equal(MountProfile.SynScanDobsonian, loaded.Mount);
            Assert.Equal(4.0, loaded.SelectedRate);
            Assert.Equal(4.0, loaded.MaximumManualRate);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ManualMotionAllowsFourDegreesButRejectsMore()
    {
        var engine = new MountEngine(new AppSettings { Mount = MountProfile.AzEq5, MaximumManualRate = 4.0 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "");
        await engine.MoveAxisAsync(MountAxis.Azimuth, 4.0);
        Assert.True(engine.Snapshot.Slewing);
        await Assert.ThrowsAsync<MountInvalidOperationException>(() => engine.MoveAxisAsync(MountAxis.Azimuth, 4.01));
        await engine.DisposeAsync();
    }
}
