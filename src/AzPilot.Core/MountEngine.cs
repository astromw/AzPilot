using System.Collections.Concurrent;

namespace AzPilot.Core;

public sealed class MountEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pollTask;
    private IMountBackend? _backend;
    private BackendState _state = new(new(0, false, false, false), new(0, false, false, false));
    private AppSettings _settings;
    private double _azimuthOffset;
    private double _altitudeOffset;
    private MultiPointAlignment _alignment = MultiPointAlignment.Empty;
    private AlignmentPoint? _singlePointAlignmentPoint;
    private int _singlePointAlignmentCount;
    private bool _connected;
    private bool _connecting;
    private bool _tracking;
    private bool _resumeTracking;
    private (double Ra, double Dec) _trackingCoordinates;
    private DateTimeOffset _trackingEpoch;
    private DateTimeOffset _lastTrackingUpdate;
    private bool _pulseGuiding;
    private int _pulseId;
    private double _parkAzimuth;
    private double _parkAltitude;
    private bool _atPark;
    private bool _parkInProgress;
    private string _status = "Ready — simulator selected";
    private string? _lastError;
    private int _communicationFailures;
    private double? _previousSafetyAltitude;
    private string _connectionText = "Not connected";

    public event Action<MountSnapshot>? SnapshotChanged;
    public event Action<string>? Logged;
    public AppSettings Settings => _settings;
    public IReadOnlyCollection<string> RecentLogs => _logs.ToArray();
    public double TargetRightAscension { get; private set; }
    public double TargetDeclination { get; private set; }

    public MountEngine(AppSettings settings)
    {
        _settings = settings;
        _pollTask = Task.Run(PollLoop);
    }

    public MountSnapshot Snapshot
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var horizontal = MechanicalToSky(_state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees);
            var az = horizontal.Azimuth;
            var alt = horizontal.Altitude;
            var eq = Astronomy.HorizontalToEquatorial(az, alt, _settings.Site, now);
            var motion = _pulseGuiding ? MotionKind.PulseGuide : _parkInProgress ? MotionKind.Park :
                _tracking ? MotionKind.Tracking : !_state.IsMoving ? MotionKind.Stopped :
                (_state.Azimuth.GoTo || _state.Altitude.GoTo) ? MotionKind.GoTo : MotionKind.Manual;
            var slewing = !_tracking && !_pulseGuiding && _state.IsMoving;
            return new(_connected, _connecting, az, alt, Angle.Wrap360(_state.Azimuth.PositionDegrees), _state.Altitude.PositionDegrees,
                eq.RightAscension, eq.Declination, slewing, _tracking, _settings.TrackingRate,
                _settings.AlignmentMode,
                _settings.AlignmentMode == AlignmentMode.MultiPoint ? _alignment.PointCount : _singlePointAlignmentCount,
                _alignment.IsActive, _alignment.ResidualDegrees, CurrentAlignmentPoints,
                _pulseGuiding, _atPark, _parkInProgress,
                motion, _connectionText, _backend?.Firmware ?? "—", _status, _lastError, now);
        }
    }

    public async Task ConnectAsync(ConnectionKind kind, string portName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _connecting = true; _lastError = null; _previousSafetyAltitude = null;
            ResetMotionState(resetPark: true);
            _status = kind == ConnectionKind.Simulator ? "Starting simulator…" : $"Opening {portName}…";
            Publish();
            if (_backend != null) { try { await _backend.DisposeAsync(); } catch { } }
            _backend = kind == ConnectionKind.Simulator ? new SimulatorBackend() : new SynScanBackend(portName, _settings.BaudRate);
            _connectionText = kind == ConnectionKind.Simulator ? "Simulator" : $"{MountCatalog.Get(_settings.Mount).Name} · {portName}";
            await _backend.ConnectAsync(cancellationToken);
            _state = await _backend.ReadStateAsync(cancellationToken);
            _connected = true; _communicationFailures = 0;
            _status = kind == ConnectionKind.Simulator ? "Simulator connected — safe to explore" : "Hardware connected — verify motion at low speed";
            Log(kind == ConnectionKind.Simulator ? "Simulator connected. No hardware commands are possible." : $"Connected to {portName} at {_settings.BaudRate} baud.");
        }
        catch (Exception ex)
        {
            _connected = false; _lastError = ex.Message; _status = "Connection failed"; Log(_lastError);
            throw;
        }
        finally { _connecting = false; Publish(); _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Closing the serial session must not change the mount's last commanded motion.
            // In particular, hardware tracking continues at its last motor-controller rates.
            if (_backend?.IsConnected == true) await _backend.DisconnectAsync();
            _connected = false;
            _status = _tracking
                ? $"Disconnected · {_settings.TrackingRate} tracking state preserved"
                : _state.IsMoving
                    ? "Disconnected · mount motion state preserved"
                    : "Disconnected · mount state preserved";
            Log("Disconnected without stopping the mount; its last motion state was preserved.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task CancelGoToAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected();
            var resume = _resumeTracking;
            var isGoTo = _parkInProgress || _resumeTracking ||
                (!_tracking && (_state.Azimuth.GoTo || _state.Altitude.GoTo));
            if (!isGoTo)
            {
                _status = _tracking
                    ? $"{_settings.TrackingRate} tracking continues · no GoTo to cancel"
                    : "No GoTo to cancel · tracking remains off";
                return;
            }

            CancelTimedOperations();
            _tracking = false;
            _parkInProgress = false;
            await _backend!.StopAllAsync();
            _state = await _backend.ReadStateAsync();
            if (resume)
            {
                await StartTrackingInternal("GoTo cancelled");
                _status = $"GoTo cancelled · {_settings.TrackingRate} tracking resumed";
                Log(_status);
            }
            else
            {
                _resumeTracking = false;
                _status = "GoTo cancelled · tracking remains off";
                Log(_status);
            }
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task StopAllAsync(string source = "User")
    {
        await _gate.WaitAsync();
        try { await StopAllInternal(); Log($"{source}: STOP sent to both axes."); }
        finally { Publish(); _gate.Release(); }
    }

    private async Task StopAllInternal()
    {
        CancelTimedOperations();
        _tracking = false; _resumeTracking = false; _parkInProgress = false;
        if (_backend?.IsConnected == true) await _backend.StopAllAsync();
        if (_backend?.IsConnected == true) _state = await _backend.ReadStateAsync();
        _status = "Stopped";
    }

    public async Task StopAxisAsync(MountAxis axis)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected(); CancelTimedOperations(); _tracking = false; _parkInProgress = false;
            await _backend!.StopAxisAsync(axis); _state = await _backend.ReadStateAsync(); _status = $"{axis} stopped";
            if (_resumeTracking && !_state.IsMoving)
                await StartTrackingInternal("manual movement complete");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task MoveAxisAsync(MountAxis axis, double signedRate)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureMovable();
            var cap = Math.Min(MountCatalog.Get(_settings.Mount).MaximumSlewRate, _settings.MaximumManualRate);
            if (Math.Abs(signedRate) > cap + 0.0001) throw new MountInvalidOperationException($"Manual rate is limited to {cap:0.###}°/s in Settings.");
            var altitude = MechanicalToSky(_state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees).Altitude;
            if (axis == MountAxis.Altitude && ((signedRate < 0 && altitude <= _settings.MinimumAltitude) || (signedRate > 0 && altitude >= 90)))
                throw new MountInvalidOperationException("Altitude safety limit reached.");
            var interruptingMotion = _tracking || _state.IsMoving;
            _resumeTracking |= _tracking;
            _tracking = false;
            if (interruptingMotion) await _backend!.StopAllAsync();
            await _backend!.MoveAxisAsync(axis, signedRate);
            _state = await _backend.ReadStateAsync();
            _status = $"{axis} manual · {signedRate:+0.###;-0.###}°/s" + (_resumeTracking ? " · tracking resumes on release" : "");
            Log($"Manual {axis}: {signedRate:+0.###;-0.###}°/s.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task SlewToAltAzAsync(double azimuth, double altitude)
    {
        new GoToTarget(Angle.Wrap360(azimuth), altitude).Validate(_settings.MinimumAltitude);
        await _gate.WaitAsync();
        try
        {
            EnsureMovable(); _resumeTracking |= _tracking; _tracking = false;
            await _backend!.StopAllAsync();
            var mechanical = SkyToMechanical(azimuth, altitude);
            var mechanicalAzimuth = mechanical.Azimuth;
            var mechanicalAltitude = mechanical.Altitude;
            _status = $"GoTo · Az {azimuth:0.00}° · Alt {altitude:0.00}°" + (_resumeTracking ? " · tracking resumes on completion" : "");
            var slow = _settings.GoToRate <= 0.5;
            await _backend.SlewToAsync(MountAxis.Azimuth, mechanicalAzimuth, slow);
            await _backend.SlewToAsync(MountAxis.Altitude, mechanicalAltitude, slow);
            _state = await _backend.ReadStateAsync();
            Log($"GoTo started: Az {azimuth:0.000}°, Alt {altitude:0.000}°.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public Task SlewToCoordinatesAsync(double rightAscension, double declination)
    {
        ValidateEquatorial(rightAscension, declination);
        TargetRightAscension = rightAscension; TargetDeclination = declination;
        var target = Astronomy.EquatorialToHorizontal(rightAscension, declination, _settings.Site, DateTimeOffset.UtcNow);
        return SlewToAltAzAsync(target.Azimuth, target.Altitude);
    }

    public void SetTarget(double rightAscension, double declination)
    {
        ValidateEquatorial(rightAscension, declination);
        TargetRightAscension = rightAscension; TargetDeclination = declination;
    }

    public Task SyncToAltAzAsync(double azimuth, double altitude) =>
        SyncToAltAzInternal(azimuth, altitude, null);

    public Task SyncToCoordinatesAsync(double rightAscension, double declination)
    {
        ValidateEquatorial(rightAscension, declination);
        var target = Astronomy.EquatorialToHorizontal(rightAscension, declination, _settings.Site, DateTimeOffset.UtcNow);
        return SyncToAltAzInternal(target.Azimuth, target.Altitude, (rightAscension, declination));
    }

    private async Task SyncToAltAzInternal(double azimuth, double altitude, (double Ra, double Dec)? equatorial)
    {
        new GoToTarget(Angle.Wrap360(azimuth), altitude).Validate(-90);
        await _gate.WaitAsync();
        try
        {
            EnsureMovable();
            _state = await _backend!.ReadStateAsync();
            if (_state.IsMoving && !_tracking)
                throw new MountInvalidOperationException("Stop manual or GoTo motion before syncing; tracking may remain active.");
            // Capture celestial coordinates at sync time. The stored point must not drift later
            // when its fixed Alt/Az values are converted using a different clock time.
            var syncTime = DateTimeOffset.UtcNow;
            var celestial = equatorial ?? Astronomy.HorizontalToEquatorial(
                azimuth, altitude, _settings.Site, syncTime);
            string alignmentStatus;
            if (_settings.AlignmentMode == AlignmentMode.MultiPoint)
            {
                _alignment = _alignment.AddPoint(new(
                    _state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees,
                    azimuth, altitude, celestial.Ra, celestial.Dec));
                if (!_alignment.IsActive)
                {
                    _azimuthOffset = Angle.ShortestDelta(_state.Azimuth.PositionDegrees, azimuth);
                    _altitudeOffset = altitude - _state.Altitude.PositionDegrees;
                }
                alignmentStatus = _alignment.IsActive
                    ? $"Alignment point {_alignment.PointCount} · 3D model RMS {_alignment.ResidualDegrees:0.000}°"
                    : $"Alignment point {_alignment.PointCount} · {Math.Max(0, 3 - _alignment.PointCount)} more needed";
            }
            else
            {
                _azimuthOffset = Angle.ShortestDelta(_state.Azimuth.PositionDegrees, azimuth);
                _altitudeOffset = altitude - _state.Altitude.PositionDegrees;
                _singlePointAlignmentPoint = new(
                    _state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees,
                    azimuth, altitude, celestial.Ra, celestial.Dec);
                _singlePointAlignmentCount = 1;
                alignmentStatus = "Alignment point 1 · single-point offset replaced";
            }
            if (_tracking)
            {
                // Keep the motors tracking, but hold the newly synced sky coordinate from now on.
                _trackingCoordinates = celestial;
                _trackingEpoch = syncTime;
            }
            _status = _tracking ? $"{alignmentStatus} · {_settings.TrackingRate} tracking continues" : alignmentStatus;
            Log(_tracking
                ? $"{alignmentStatus}; {_settings.TrackingRate} tracking continues on the synced coordinate."
                : $"{alignmentStatus} at Az {azimuth:0.000}°, Alt {altitude:0.000}°.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task ClearAlignmentPointsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Clearing removes the alignment itself, not just its visible point list.
            // Restore the raw mechanical reference; rebasing below prevents a tracking jump.
            _azimuthOffset = 0;
            _altitudeOffset = 0;
            _singlePointAlignmentCount = 0;
            _singlePointAlignmentPoint = null;
            _alignment = MultiPointAlignment.Empty;
            if (_tracking)
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = Snapshot;
                _trackingCoordinates = Astronomy.HorizontalToEquatorial(snapshot.Azimuth, snapshot.Altitude, _settings.Site, now);
                _trackingEpoch = now;
                _lastTrackingUpdate = DateTimeOffset.MinValue;
            }
            _status = $"{_settings.AlignmentMode} alignment cleared · raw mechanical reference restored";
            Log($"Cleared {_settings.AlignmentMode} alignment points and removed their pointing effect.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task SetParkAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected();
            if (_atPark) { _atPark = false; }
            if (_state.IsMoving || _tracking || _pulseGuiding || _parkInProgress)
                throw new MountInvalidOperationException("Stop motion and tracking before setting the park position.");
            _parkAzimuth = _state.Azimuth.PositionDegrees;
            _parkAltitude = _state.Altitude.PositionDegrees;
            _status = "Park position saved for this session";
            Log($"Park set at mechanical Az {_parkAzimuth:0.000}°, Alt {_parkAltitude:0.000}°.");
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task ParkAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected();
            if (_atPark) { _status = "Already parked"; return; }
            if (_pulseGuiding || _parkInProgress) throw new MountInvalidOperationException("Another timed operation is active.");
            _tracking = false; _resumeTracking = false;
            await _backend!.StopAllAsync();
            _parkInProgress = true; _atPark = false;
            _status = "Parking · moving to saved mechanical position";
            var slow = _settings.GoToRate <= 0.5;
            await _backend.SlewToAsync(MountAxis.Azimuth, _parkAzimuth, slow);
            await _backend.SlewToAsync(MountAxis.Altitude, _parkAltitude, slow);
            _state = await _backend.ReadStateAsync();
            CompleteParkIfStopped();
            Log("Park slew started.");
        }
        catch
        {
            _parkInProgress = false; _tracking = false; _resumeTracking = false;
            try { if (_backend?.IsConnected == true) { await _backend.StopAllAsync(); _state = await _backend.ReadStateAsync(); } } catch { }
            throw;
        }
        finally { Publish(); _gate.Release(); }
    }

    public async Task UnparkAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected();
            if (_state.IsMoving || _parkInProgress) throw new MountInvalidOperationException("Stop the park slew before unparking.");
            _atPark = false; _status = "Unparked — ready for motion";
            Log("Mount unparked.");
        }
        finally { Publish(); _gate.Release(); }
    }

    private void CompleteParkIfStopped()
    {
        if (!_parkInProgress || _state.IsMoving) return;
        _parkInProgress = false;
        var azError = Math.Abs(Angle.ShortestDelta(_state.Azimuth.PositionDegrees, _parkAzimuth));
        var altError = Math.Abs(_state.Altitude.PositionDegrees - _parkAltitude);
        _atPark = azError < 0.05 && altError < 0.05;
        _status = _atPark ? "Parked — tracking and motion locked" : "Park ended away from the saved position";
        if (!_atPark) _lastError = "Park position was not reached; inspect the mount before unparking or retrying.";
        Log(_atPark ? "Park complete. Motion is locked until Unpark." : "Park did not reach its target.");
    }

    public async Task SetTrackingAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureConnected();
            if (!enabled) { await StopAllInternal(); _status = "Tracking off"; return; }
            await StartTrackingInternal("user request");
        }
        finally { Publish(); _gate.Release(); }
    }

    private async Task StartTrackingInternal(string reason)
    {
        EnsureMovable();
        _state = await _backend!.ReadStateAsync();
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot;
        _trackingCoordinates = Astronomy.HorizontalToEquatorial(snapshot.Azimuth, snapshot.Altitude, _settings.Site, now);
        _trackingEpoch = now;
        _tracking = true; _resumeTracking = false; _lastTrackingUpdate = DateTimeOffset.MinValue;
        try
        {
            // Validate and start both axes as one operation. Any failure leaves tracking off
            // and both motors stopped instead of reporting a partially started tracking state.
            await ApplyTrackingInternal();
            _state = await _backend.ReadStateAsync();
            _status = $"{_settings.TrackingRate} tracking · AZ software rates with position feedback";
            Log($"{_settings.TrackingRate} tracking started ({reason}) using variable Alt/Az rates and closed-loop position correction.");
        }
        catch
        {
            _tracking = false; _resumeTracking = false; CancelTimedOperations();
            try { await _backend.StopAllAsync(); _state = await _backend.ReadStateAsync(); } catch { }
            _status = "Tracking start rejected — both axes stopped";
            throw;
        }
    }

    public async Task SetTrackingRateAsync(TrackingRateMode rate)
    {
        if (!Enum.IsDefined(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        await _gate.WaitAsync();
        try
        {
            if (_settings.TrackingRate == rate) return;
            _settings = _settings with { TrackingRate = rate };
            if (_tracking && _backend?.IsConnected == true)
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = Snapshot;
                _trackingCoordinates = Astronomy.HorizontalToEquatorial(snapshot.Azimuth, snapshot.Altitude, _settings.Site, now);
                _trackingEpoch = now;
                _lastTrackingUpdate = DateTimeOffset.MinValue;
                await ApplyTrackingInternal();
                _state = await _backend.ReadStateAsync();
                _status = $"{rate} tracking · AZ software rates with position feedback";
                Log($"Tracking rate changed to {rate}; the current sky coordinate is retained.");
            }
        }
        finally { Publish(); _gate.Release(); }
    }

    public void SetGuideRates(double azimuth, double altitude)
    {
        if (_pulseGuiding) throw new MountInvalidOperationException("Cannot change guide rates during a pulse.");
        if (!double.IsFinite(azimuth) || !double.IsFinite(altitude) ||
            azimuth is <= 0 or > 0.1 || altitude is <= 0 or > 0.1)
            throw new ArgumentOutOfRangeException(nameof(azimuth), "Guide rates must be greater than 0 and no more than 0.1°/s.");
        _settings = _settings with { GuideRateAzimuth = azimuth, GuideRateAltitude = altitude };
        Publish();
    }

    public async Task PulseGuideAsync(GuideDirection direction, int durationMilliseconds)
    {
        if (durationMilliseconds is < 10 or > 30000) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds), "Pulse duration must be 10–30000 ms.");
        await _gate.WaitAsync();
        var id = 0;
        try
        {
            EnsureMovable();
            if (!_tracking) throw new MountInvalidOperationException("Pulse guiding requires active tracking.");
            var now = DateTimeOffset.UtcNow;
            var seconds = durationMilliseconds / 1000.0;
            var baseRates = ComputeTrackingRates(now);
            var azimuthCorrection = direction switch
            {
                GuideDirection.Right => _settings.GuideRateAzimuth * seconds,
                GuideDirection.Left => -_settings.GuideRateAzimuth * seconds,
                GuideDirection.Up or GuideDirection.Down => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(direction))
            };
            var altitudeCorrection = direction switch
            {
                GuideDirection.Up => _settings.GuideRateAltitude * seconds,
                GuideDirection.Down => -_settings.GuideRateAltitude * seconds,
                GuideDirection.Right or GuideDirection.Left => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(direction))
            };
            var pulseAzimuth = Angle.Wrap360(baseRates.Current.Azimuth + azimuthCorrection);
            var pulseAltitude = baseRates.Current.Altitude + altitudeCorrection;
            var azRate = baseRates.AzimuthRate + azimuthCorrection / seconds;
            var altRate = baseRates.AltitudeRate + altitudeCorrection / seconds;
            ValidateTrackingRates(azRate, altRate, pulseAltitude);

            _pulseGuiding = true; id = ++_pulseId;
            await _backend!.MoveAxisAsync(MountAxis.Azimuth, azRate);
            await _backend.MoveAxisAsync(MountAxis.Altitude, altRate);
            _trackingCoordinates = Astronomy.HorizontalToEquatorial(pulseAzimuth, pulseAltitude, _settings.Site, now);
            _trackingEpoch = now;
            _lastTrackingUpdate = DateTimeOffset.UtcNow;
            _state = await _backend.ReadStateAsync();
            _status = $"Pulse guide {direction} · {durationMilliseconds} ms";
            Log($"Pulse guide {direction} for {durationMilliseconds} ms; AZ correction {azimuthCorrection:0.000000}°, Alt correction {altitudeCorrection:0.000000}°.");
            _ = CompletePulseAfterAsync(id, durationMilliseconds);
        }
        catch
        {
            if (id != 0 && id == _pulseId)
            {
                _pulseGuiding = false; _tracking = false; _resumeTracking = false;
                try { if (_backend?.IsConnected == true) { await _backend.StopAllAsync(); _state = await _backend.ReadStateAsync(); } } catch { }
            }
            throw;
        }
        finally { Publish(); _gate.Release(); }
    }

    private async Task CompletePulseAfterAsync(int id, int durationMilliseconds)
    {
        try { await Task.Delay(durationMilliseconds); } catch { return; }
        await _gate.WaitAsync();
        try
        {
            if (id != _pulseId) return;
            if (_tracking && _backend?.IsConnected == true)
            {
                var rates = ComputeTrackingRates(DateTimeOffset.UtcNow);
                ValidateTrackingRates(rates.AzimuthRate, rates.AltitudeRate, rates.Current.Altitude, rates.NextAltitude);
                await _backend.MoveAxisAsync(MountAxis.Azimuth, rates.AzimuthRate);
                await _backend.MoveAxisAsync(MountAxis.Altitude, rates.AltitudeRate);
                _state = await _backend.ReadStateAsync();
                _status = $"Pulse complete — {_settings.TrackingRate} tracking resumed";
            }
            Log("Pulse guide complete.");
        }
        catch (Exception ex)
        {
            _tracking = false; _resumeTracking = false; _lastError = ex.Message; _status = "Pulse completion failed — motion stopped";
            try { if (_backend?.IsConnected == true) await _backend.StopAllAsync(); } catch { }
            Log(_status);
        }
        finally
        {
            if (id == _pulseId) _pulseGuiding = false;
            Publish(); _gate.Release();
        }
    }

    private (double AzimuthRate, double AltitudeRate, (double Altitude, double Azimuth) Current, double NextAltitude) ComputeTrackingRates(
        DateTimeOffset now, bool includePositionFeedback = false)
    {
        var coordinates = TrackingCoordinatesAt(now);
        var current = Astronomy.EquatorialToHorizontal(coordinates.Ra, coordinates.Dec, _settings.Site, now);
        var nextCoordinates = TrackingCoordinatesAt(now.AddSeconds(1));
        var next = Astronomy.EquatorialToHorizontal(nextCoordinates.Ra, nextCoordinates.Dec, _settings.Site, now.AddSeconds(1));
        var azimuthRate = Angle.ShortestDelta(current.Azimuth, next.Azimuth);
        var altitudeRate = next.Altitude - current.Altitude;

        if (includePositionFeedback)
        {
            // Feed-forward rates alone accumulate error from timer quantization, command latency,
            // and motor-rate rounding. Correct the measured pointing error gently without a jump.
            var actual = MechanicalToSky(_state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees);
            var actualAzimuth = actual.Azimuth;
            var actualAltitude = actual.Altitude;
            var azimuthError = Angle.ShortestDelta(actualAzimuth, current.Azimuth);
            var altitudeError = current.Altitude - actualAltitude;
            azimuthRate += Math.Clamp(azimuthError / 2.0, -0.05, 0.05);
            altitudeRate += Math.Clamp(altitudeError / 2.0, -0.05, 0.05);
        }
        return (azimuthRate, altitudeRate, current, next.Altitude);
    }

    private (double Ra, double Dec) TrackingCoordinatesAt(DateTimeOffset now)
    {
        var meanRaMotionDegreesPerDay = _settings.TrackingRate switch
        {
            TrackingRateMode.Lunar => 13.176396,
            TrackingRateMode.Solar => 0.985647,
            _ => 0.0
        };
        var elapsedDays = (now - _trackingEpoch).TotalSeconds / 86400.0;
        var ra = (_trackingCoordinates.Ra + meanRaMotionDegreesPerDay * elapsedDays / 15.0) % 24.0;
        return (ra < 0 ? ra + 24.0 : ra, _trackingCoordinates.Dec);
    }

    private void ValidateTrackingRates(double azRate, double altRate, double altitude, double? nextAltitude = null)
    {
        if (Math.Abs(azRate) > 1 || Math.Abs(altRate) > 1)
            throw new MountInvalidOperationException("Required AZ rate exceeds the 1°/s tracking/pulse safety ceiling.");
        if (altitude < _settings.MinimumAltitude || altitude > 90 ||
            (nextAltitude.HasValue && (nextAltitude.Value < _settings.MinimumAltitude || nextAltitude.Value > 90)))
            throw new MountInvalidOperationException("Tracking or pulse would cross the altitude safety limit.");
    }

    private async Task ApplyTrackingInternal()
    {
        if (!_tracking || _pulseGuiding || _backend?.IsConnected != true) return;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTrackingUpdate < TimeSpan.FromSeconds(1)) return;
        _lastTrackingUpdate = now;
        var rates = ComputeTrackingRates(now, includePositionFeedback: true);
        ValidateTrackingRates(rates.AzimuthRate, rates.AltitudeRate, rates.Current.Altitude, rates.NextAltitude);
        await _backend.MoveAxisAsync(MountAxis.Azimuth, rates.AzimuthRate);
        await _backend.MoveAxisAsync(MountAxis.Altitude, rates.AltitudeRate);
    }

    public void UpdateSettings(AppSettings settings)
    {
        if (settings.MinimumAltitude is < -90 or > 80) throw new ArgumentOutOfRangeException(nameof(settings.MinimumAltitude));
        if (settings.MaximumManualRate is <= 0 or > MountCatalog.ApplicationMaximumSlewRate) throw new ArgumentOutOfRangeException(nameof(settings.MaximumManualRate));
        if (settings.GuideRateAzimuth is <= 0 or > 0.1 || settings.GuideRateAltitude is <= 0 or > 0.1)
            throw new ArgumentOutOfRangeException(nameof(settings.GuideRateAzimuth));
        if (settings.Site.Latitude is < -90 or > 90 || settings.Site.Longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(settings.Site));
        if (!Enum.IsDefined(settings.AlignmentMode)) throw new ArgumentOutOfRangeException(nameof(settings.AlignmentMode));
        var alignmentModeChanged = settings.AlignmentMode != _settings.AlignmentMode;
        if (settings.AlignmentMode == AlignmentMode.SinglePoint && alignmentModeChanged)
        {
            var current = MechanicalToSky(_state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees);
            _azimuthOffset = Angle.ShortestDelta(_state.Azimuth.PositionDegrees, current.Azimuth);
            _altitudeOffset = current.Altitude - _state.Altitude.PositionDegrees;
        }
        _settings = settings;
        if (alignmentModeChanged && _tracking)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = Snapshot;
            _trackingCoordinates = Astronomy.HorizontalToEquatorial(snapshot.Azimuth, snapshot.Altitude, _settings.Site, now);
            _trackingEpoch = now;
            _lastTrackingUpdate = DateTimeOffset.MinValue;
        }
        Publish();
    }

    private async Task PollLoop()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, _lifetime.Token);
                if (!_connected || _backend?.IsConnected != true) continue;
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    _state = await _backend.ReadStateAsync(_lifetime.Token);
                    await ApplyTrackingInternal();
                    CompleteParkIfStopped();
                    if (_resumeTracking && !_state.IsMoving && !_parkInProgress)
                    {
                        try { await StartTrackingInternal("movement complete"); }
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;
                            _status = $"Tracking resume blocked — {ex.Message}";
                            Log(_status);
                        }
                    }
                    var altitude = MechanicalToSky(_state.Azimuth.PositionDegrees, _state.Altitude.PositionDegrees).Altitude;
                    // Being outside the configured range is not itself a reason to inhibit recovery.
                    // Compare measured positions so manual slews and park GoTos may return toward safety,
                    // while motion farther below the minimum or farther above 90° is still stopped.
                    const double directionEpsilon = 0.0001;
                    var movingFartherBelow = altitude < _settings.MinimumAltitude &&
                        _previousSafetyAltitude.HasValue && altitude < _previousSafetyAltitude.Value - directionEpsilon;
                    var movingFartherAbove = altitude > 90 &&
                        _previousSafetyAltitude.HasValue && altitude > _previousSafetyAltitude.Value + directionEpsilon;
                    _previousSafetyAltitude = altitude;
                    if (_state.Altitude.Moving && (movingFartherBelow || movingFartherAbove))
                    {
                        await _backend.StopAxisAsync(MountAxis.Altitude);
                        CancelTimedOperations(); _tracking = false; _resumeTracking = false; _parkInProgress = false; _atPark = false;
                        _status = "Altitude limit — motion farther outside the safe range stopped";
                    }
                    _communicationFailures = 0;
                    if (!_status.StartsWith("Tracking resume blocked", StringComparison.Ordinal)) _lastError = null;
                }
                finally { _gate.Release(); }
                Publish();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _communicationFailures++; _lastError = ex.Message; _status = "Communication problem";
                Log($"Communication warning {_communicationFailures}: {ex.Message}");
                try { if (_backend?.IsConnected == true) await _backend.StopAllAsync(); } catch { }
                CancelTimedOperations(); _tracking = false; _resumeTracking = false; _parkInProgress = false; _atPark = false;
                if (_communicationFailures >= 3)
                {
                    _connected = false; _status = "Connection lost";
                    if (_settings.AutoReconnect && _backend != null)
                    {
                        await Task.Delay(1000);
                        try
                        {
                            await _backend.DisconnectAsync(); await _backend.ConnectAsync(_lifetime.Token);
                            _state = await _backend.ReadStateAsync(_lifetime.Token);
                            _connected = true; _communicationFailures = 0; _status = "Reconnected — motion remains stopped";
                            Log("Connection recovered. Tracking remains off; no motion was resumed automatically.");
                        }
                        catch (Exception reconnectError) { _lastError = reconnectError.Message; }
                    }
                }
                Publish();
            }
        }
    }

    private IReadOnlyList<AlignmentPoint> CurrentAlignmentPoints
    {
        get
        {
            if (_settings.AlignmentMode == AlignmentMode.MultiPoint) return _alignment.Points;
            return _singlePointAlignmentPoint is { } point ? [point] : Array.Empty<AlignmentPoint>();
        }
    }

    private (double Azimuth, double Altitude) MechanicalToSky(double azimuth, double altitude)
    {
        if (_settings.AlignmentMode == AlignmentMode.MultiPoint && _alignment.IsActive)
            return _alignment.ToSky(azimuth, altitude);
        return (Angle.Wrap360(azimuth + _azimuthOffset), altitude + _altitudeOffset);
    }

    private (double Azimuth, double Altitude) SkyToMechanical(double azimuth, double altitude)
    {
        if (_settings.AlignmentMode == AlignmentMode.MultiPoint && _alignment.IsActive)
            return _alignment.ToMechanical(azimuth, altitude);
        return (Angle.Wrap360(azimuth - _azimuthOffset), altitude - _altitudeOffset);
    }

    private void EnsureConnected() { if (!_connected || _backend?.IsConnected != true) throw new MountException("Connect to the simulator or mount first."); }
    private void EnsureMovable()
    {
        EnsureConnected();
        if (_atPark) throw new MountParkedException();
        if (_parkInProgress) throw new MountInvalidOperationException("Park is in progress. Abort it before another motion command.");
        if (_pulseGuiding) throw new MountInvalidOperationException("A pulse guide is active. Wait for completion or abort motion first.");
    }
    private void CancelTimedOperations() { _pulseId++; _pulseGuiding = false; }
    private void ResetMotionState(bool resetPark)
    {
        CancelTimedOperations(); _tracking = false; _resumeTracking = false; _parkInProgress = false;
        if (resetPark) _atPark = false;
    }
    private static void ValidateEquatorial(double ra, double dec)
    {
        if (!double.IsFinite(ra) || ra is < 0 or >= 24) throw new ArgumentOutOfRangeException(nameof(ra), "Right ascension must be in the range 0–24 hours.");
        if (!double.IsFinite(dec) || dec is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(dec), "Declination must be in the range −90° to +90°.");
    }
    private void Publish() { try { SnapshotChanged?.Invoke(Snapshot); } catch { } }
    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _logs.Enqueue(line); while (_logs.Count > 150) _logs.TryDequeue(out _);
        Logged?.Invoke(line);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _pulseId++;
        try { await _pollTask; } catch { }
        if (_backend != null) { try { await _backend.DisposeAsync(); } catch { } }
        _gate.Dispose(); _lifetime.Dispose();
    }
}
