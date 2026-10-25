namespace AzPilot.Core;

public sealed class SynScanBackend : IMountBackend
{
    private readonly SerialTransport _transport = new();
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly AxisParameters[] _axes = [new(), new()];
    private bool _initialized;
    public bool IsConnected => _initialized && _transport.IsOpen;
    public string Description => $"USB · {_portName} · {_baudRate} baud";
    public string Firmware { get; private set; } = "—";

    public SynScanBackend(string portName, int baudRate) { _portName = portName; _baudRate = baudRate; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _transport.Open(_portName, _baudRate);
        try
        {
            foreach (var axis in new[] { MountAxis.Azimuth, MountAxis.Altitude })
            {
                var p = _axes[(int)axis - 1];
                p.CountsPerRevolution = await QueryNumberWithRetry(axis, 'a', cancellationToken);
                p.TimerFrequency = await QueryNumberWithRetry(axis, 'b', cancellationToken);
                p.HighSpeedRatio = (int)await QueryNumberWithRetry(axis, 'g', cancellationToken);
                if (p.CountsPerRevolution <= 0 || p.TimerFrequency <= 0) throw new MountException("The mount returned invalid axis parameters.");
                if (axis == MountAxis.Azimuth)
                {
                    var version = await QueryNumberWithRetry(axis, 'e', cancellationToken);
                    Firmware = $"{(version >> 16) & 0xff}.{(version >> 8) & 0xff:X2}.{version & 0xff:X2}";
                }
                await _transport.QueryAsync(SynScanProtocol.Command(axis, 'F'), cancellationToken);
            }
            _initialized = true;
        }
        catch { _transport.Close(); _initialized = false; throw; }
    }

    private async Task<long> QueryNumberWithRetry(MountAxis axis, char command, CancellationToken ct)
    {
        try { return await QueryNumber(axis, command, ct); }
        catch (MountException) when (!ct.IsCancellationRequested) { await Task.Delay(120, ct); return await QueryNumber(axis, command, ct); }
    }
    private async Task<long> QueryNumber(MountAxis axis, char command, CancellationToken ct) =>
        SynScanProtocol.DecodeLittleEndian(await _transport.QueryAsync(SynScanProtocol.Command(axis, command), ct));

    public async Task<BackendState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var az = await ReadAxis(MountAxis.Azimuth, cancellationToken);
        var alt = await ReadAxis(MountAxis.Altitude, cancellationToken);
        return new(az, alt);
    }

    private async Task<BackendAxisState> ReadAxis(MountAxis axis, CancellationToken ct)
    {
        var position = SynScanProtocol.DecodeLittleEndian(await _transport.QueryAsync(SynScanProtocol.Command(axis, 'j'), ct));
        var status = await _transport.QueryAsync(SynScanProtocol.Command(axis, 'f'), ct);
        if (status.Length < 2) throw new MountException("The mount returned a short axis-status response.");
        var mode = Convert.ToInt32(status[0].ToString(), 16);
        var state = Convert.ToInt32(status[1].ToString(), 16);
        var degrees = SynScanProtocol.StepsToDegrees(position, _axes[(int)axis - 1].CountsPerRevolution);
        return new(degrees, (state & 1) != 0, (mode & 1) == 0, (mode & 4) != 0);
    }

    public async Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (Math.Abs(degreesPerSecond) < 0.00001) { await StopAxisAsync(axis, true, cancellationToken); return; }
        if (Math.Abs(degreesPerSecond) > 4.0) throw new ArgumentOutOfRangeException(nameof(degreesPerSecond), "Rate exceeds the 4°/s safety ceiling.");
        var p = _axes[(int)axis - 1];
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'K'), cancellationToken);
        await WaitForStop(axis, cancellationToken);
        var highSpeed = Math.Abs(degreesPerSecond) > 128 * SynScanProtocol.SiderealDegreesPerSecond;
        var direction = degreesPerSecond > 0 ? 0 : 1;
        var mode = (highSpeed ? 0x30 : 0x10) | direction;
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'G', mode.ToString("X2")), cancellationToken);
        var period = SynScanProtocol.RateToTimerPeriod(degreesPerSecond, p.CountsPerRevolution, p.TimerFrequency, p.HighSpeedRatio);
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'I', SynScanProtocol.EncodeLittleEndian(period, 3)), cancellationToken);
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'J'), cancellationToken);
    }

    public async Task SlewToAsync(MountAxis axis, double targetDegrees, bool slow, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var state = await ReadStateAsync(cancellationToken);
        var current = axis == MountAxis.Azimuth ? state.Azimuth.PositionDegrees : state.Altitude.PositionDegrees;
        var delta = axis == MountAxis.Azimuth ? Angle.ShortestDelta(current, targetDegrees) : targetDegrees - current;
        var p = _axes[(int)axis - 1];
        var steps = SynScanProtocol.DegreesToSteps(delta, p.CountsPerRevolution);
        if (steps == 0) return;
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'K'), cancellationToken);
        await WaitForStop(axis, cancellationToken);
        var direction = steps < 0 ? 1 : 0;
        steps = Math.Abs(steps);
        var mode = (slow ? 0x02 : 0x00) | direction;
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'G', mode.ToString("X2")), cancellationToken);
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'H', SynScanProtocol.EncodeLittleEndian(steps, 3)), cancellationToken);
        var brake = Math.Min(3500, Math.Max(1, steps / 3));
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'M', SynScanProtocol.EncodeLittleEndian(brake, 3)), cancellationToken);
        await _transport.QueryAsync(SynScanProtocol.Command(axis, 'J'), cancellationToken);
    }


    private async Task WaitForStop(MountAxis axis, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (!(await ReadAxis(axis, cancellationToken)).Moving) return;
            await Task.Delay(50, cancellationToken);
        }
        throw new MountException($"{axis} did not stop within 3 seconds. Check the mount before issuing another command.");
    }
    public async Task StopAxisAsync(MountAxis axis, bool immediate = true, CancellationToken cancellationToken = default)
    {
        if (!_transport.IsOpen) return;
        await _transport.QueryAsync(SynScanProtocol.Command(axis, immediate ? 'L' : 'K'), cancellationToken);
    }
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        if (!_transport.IsOpen) return;
        Exception? failure = null;
        try { await StopAxisAsync(MountAxis.Azimuth, true, cancellationToken); } catch (Exception ex) { failure = ex; }
        try { await StopAxisAsync(MountAxis.Altitude, true, cancellationToken); } catch (Exception ex) { failure ??= ex; }
        if (failure != null) throw failure;
    }
    public Task DisconnectAsync() { _initialized = false; _transport.Close(); return Task.CompletedTask; }
    private void EnsureConnected() { if (!IsConnected) throw new MountException("The mount is not connected."); }
    public async ValueTask DisposeAsync() { if (_transport.IsOpen) { try { await StopAllAsync(); } catch { } } _transport.Dispose(); }
    private sealed class AxisParameters { public long CountsPerRevolution; public long TimerFrequency; public int HighSpeedRatio; }
}
