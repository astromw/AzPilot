using System.Diagnostics;

namespace AzPilot.Core;

public sealed class SimulatorBackend : IMountBackend
{
    private readonly SimAxis _az = new(0, true);
    private readonly SimAxis _alt = new(0, false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastUpdate;
    public bool IsConnected { get; private set; }
    public string Description => "Built-in simulator · no hardware commands";
    public string Firmware => "Simulator 1.0";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        IsConnected = true; _lastUpdate = _clock.Elapsed;
    }
    public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }

    public Task<BackendState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new MountException("The simulator is not connected.");
        Update();
        return Task.FromResult(new BackendState(_az.State(), _alt.State()));
    }

    public Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default)
    {
        EnsureConnected(); Update(); Get(axis).Move(degreesPerSecond); return Task.CompletedTask;
    }
    public Task SlewToAsync(MountAxis axis, double targetDegrees, bool slow, CancellationToken cancellationToken = default)
    {
        EnsureConnected(); Update(); Get(axis).GoTo(targetDegrees, slow ? 0.45 : 2.0); return Task.CompletedTask;
    }
    public Task StopAxisAsync(MountAxis axis, bool immediate = true, CancellationToken cancellationToken = default)
    { EnsureConnected(); Update(); Get(axis).Stop(); return Task.CompletedTask; }
    public Task StopAllAsync(CancellationToken cancellationToken = default)
    { EnsureConnected(); Update(); _az.Stop(); _alt.Stop(); return Task.CompletedTask; }
    private SimAxis Get(MountAxis axis) => axis == MountAxis.Azimuth ? _az : _alt;
    private void Update() { var now = _clock.Elapsed; var dt = Math.Min(2, (now - _lastUpdate).TotalSeconds); _lastUpdate = now; _az.Update(dt); _alt.Update(dt); }
    private void EnsureConnected() { if (!IsConnected) throw new MountException("The simulator is not connected."); }
    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private sealed class SimAxis(double initial, bool wrap)
    {
        private double _position = initial;
        private double _rate;
        private double? _target;
        private bool _goTo;
        public void Move(double rate) { _rate = rate; _target = null; _goTo = false; }
        public void GoTo(double target, double speed)
        {
            var delta = Math.Abs(target - _position) > 180 ? Angle.ShortestDelta(_position, target) : target - _position;
            if (Math.Abs(delta) < 0.00001) { Stop(); return; }
            _rate = Math.Sign(delta) * speed; _target = target; _goTo = true;
        }
        public void Stop() { _rate = 0; _target = null; _goTo = false; }
        public void Update(double seconds)
        {
            if (_rate == 0) return;
            var next = _position + _rate * seconds;
            if (_target.HasValue)
            {
                var before = Angle.ShortestDelta(_position, _target.Value);
                var after = Angle.ShortestDelta(next, _target.Value);
                if (Math.Sign(before) != Math.Sign(after) || Math.Abs(after) < Math.Abs(_rate * seconds) + 0.00001)
                { _position = wrap ? Angle.Wrap360(_target.Value) : _target.Value; Stop(); return; }
            }
            _position = wrap ? Angle.Wrap360(next) : next;
        }
        public BackendAxisState State() => new(wrap ? Angle.Wrap360(_position) : _position, _rate != 0, _goTo, Math.Abs(_rate) > 0.55);
    }
}
