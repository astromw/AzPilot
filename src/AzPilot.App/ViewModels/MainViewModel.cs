using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using AzPilot.Alpaca;
using AzPilot.Core;

namespace AzPilot.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly MountEngine _engine;
    private readonly AlpacaServer _alpaca;
    private MountSnapshot _snapshot;
    private string _page = "Dashboard";
    private string? _uiError;
    private bool _isSimulator;
    private string _selectedPort;
    private bool _hardwareConfirmed;
    private bool _nightVision;
    private double _selectedRate;
    private MountProfileOption _selectedMount = MountCatalog.Profiles[0];
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> PortNames { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();
    public double[] Rates { get; } = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 3.0, 4.0];
    public IReadOnlyList<MountProfileOption> MountOptions => MountCatalog.Profiles;
    public AlignmentMode[] AlignmentOptions { get; } = Enum.GetValues<AlignmentMode>();
    public double[] ManualRateCeilings { get; } = [0.25, 0.5, 1.0, 2.0, 3.0, 4.0];
    public double[] GoToRates { get; } = [0.25, 0.5, 1.0, 2.0, 3.0, 4.0];

    public MainViewModel() : this(new SettingsStore()) { }
    public MainViewModel(SettingsStore store)
    {
        _store = store;
        var settings = store.Load();
        _engine = new MountEngine(settings);
        _alpaca = new AlpacaServer(_engine);
        _snapshot = _engine.Snapshot;
        _isSimulator = settings.ConnectionKind == ConnectionKind.Simulator;
        _selectedPort = settings.PortName;
        _hardwareConfirmed = settings.HardwareMovementConfirmed;
        _selectedRate = settings.SelectedRate;
        _selectedMount = MountCatalog.Get(settings.Mount);
        MinimumAltitude = (decimal)settings.MinimumAltitude;
        MaximumManualRate = settings.MaximumManualRate;
        GoToRate = settings.GoToRate;
        GuideRateAzimuth = (decimal)settings.GuideRateAzimuth;
        GuideRateAltitude = (decimal)settings.GuideRateAltitude;
        AutoReconnect = settings.AutoReconnect;
        AlpacaEnabled = settings.AlpacaEnabled;
        AlpacaPort = (decimal)settings.AlpacaPort;
        SiteLatitude = (decimal)settings.Site.Latitude;
        SiteLongitude = (decimal)settings.Site.Longitude;
        SiteElevation = (decimal)settings.Site.Elevation;
        NightVision = settings.NightVision;
        AlignmentMode = settings.AlignmentMode;

        _engine.SnapshotChanged += snapshot => Dispatcher.UIThread.Post(() => { Snapshot = snapshot; });
        _engine.Logged += line =>
        {
            StartupDiagnostics.Info(line);
            Dispatcher.UIThread.Post(() => { Logs.Add(line); if (Logs.Count > 150) Logs.RemoveAt(0); });
        };
        foreach (var log in _engine.RecentLogs) Logs.Add(log);
        RefreshPorts();

        ConnectCommand = new AsyncRelayCommand(ConnectDisconnect);
        CancelGoToCommand = new AsyncRelayCommand(() => Run(_engine.CancelGoToAsync));
        StopCommand = new AsyncRelayCommand(() => _engine.StopAllAsync());
        GoToAltAzCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.SlewToAltAzAsync(Parse(TargetAzimuth), Parse(TargetAltitude)); }));
        GoToEquatorialCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.SlewToCoordinatesAsync(Parse(TargetRightAscension), Parse(TargetDeclination)); }));
        SyncAltAzCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.SyncToAltAzAsync(Parse(TargetAzimuth), Parse(TargetAltitude)); }));
        SyncEquatorialCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.SyncToCoordinatesAsync(Parse(TargetRightAscension), Parse(TargetDeclination)); }));
        SetHomeCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.SyncToAltAzAsync(0, 0); }));
        ClearAlignmentCommand = new AsyncRelayCommand(() => Run(_engine.ClearAlignmentPointsAsync));
        SiderealTrackingCommand = new AsyncRelayCommand(() => StartTracking(TrackingRateMode.Sidereal));
        LunarTrackingCommand = new AsyncRelayCommand(() => StartTracking(TrackingRateMode.Lunar));
        SolarTrackingCommand = new AsyncRelayCommand(() => StartTracking(TrackingRateMode.Solar));
        StopTrackingCommand = new AsyncRelayCommand(() => Run(() => _engine.SetTrackingAsync(false)));
        SetParkCommand = new AsyncRelayCommand(() => Run(_engine.SetParkAsync));
        ParkCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return _engine.ParkAsync(); }));
        UnparkCommand = new AsyncRelayCommand(() => Run(_engine.UnparkAsync));
        PulseUpCommand = new AsyncRelayCommand(() => Pulse(GuideDirection.Up));
        PulseDownCommand = new AsyncRelayCommand(() => Pulse(GuideDirection.Down));
        PulseRightCommand = new AsyncRelayCommand(() => Pulse(GuideDirection.Right));
        PulseLeftCommand = new AsyncRelayCommand(() => Pulse(GuideDirection.Left));
        SaveGuideRatesCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return Task.CompletedTask; }));
        SaveSettingsCommand = new AsyncRelayCommand(() => Run(() => { SaveSettings(); return Task.CompletedTask; }));
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        OpenAlpacaCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(AlpacaEndpoint + "/setup") { UseShellExecute = true }));
        ShowDashboardCommand = new RelayCommand(() => Page = "Dashboard");
        ShowAlignCommand = new RelayCommand(() => Page = "Align");
        ShowSettingsCommand = new RelayCommand(() => Page = "Settings");
        ShowAscomCommand = new RelayCommand(() => Page = "ASCOM");
        ShowDiagnosticsCommand = new RelayCommand(() => Page = "Diagnostics");
    }

    public async Task StartAsync()
    {
        try
        {
            if (AlpacaEnabled) await _alpaca.StartAsync((int)AlpacaPort);
            StartupDiagnostics.Info($"ASCOM service started at {AlpacaEndpoint}.");
        }
        catch (Exception ex)
        {
            UiError = $"ASCOM could not start: {ex.Message}";
            StartupDiagnostics.Info($"ASCOM startup failed: {ex}");
        }
        StartupDiagnostics.Info("Startup complete; waiting for a user-initiated connection.");
        OnChanged();
    }

    public MountSnapshot Snapshot { get => _snapshot; private set { _snapshot = value; OnChanged(); } }
    public string Page { get => _page; set { _page = value; OnChanged(); } }
    public string? UiError { get => _uiError; set { _uiError = value; OnChanged(); } }
    public bool IsDashboard => Page == "Dashboard";
    public bool IsAlign => Page == "Align";
    public bool IsSettings => Page == "Settings";
    public bool IsAscom => Page == "ASCOM";
    public bool IsDiagnostics => Page == "Diagnostics";
    public string PageSubtitle => Page switch {
        "Dashboard" => "Live position and direct mount control",
        "Align" => "Establish and refine the AZ pointing reference",
        "Settings" => "Observing site, motion limits and USB connection",
        "ASCOM" => "Connect planetarium and imaging software through Alpaca",
        _ => "Connection activity and protocol diagnostics" };

    public bool IsSimulator { get => _isSimulator; set { _isSimulator = value; OnChanged(); } }
    public bool IsHardware { get => !IsSimulator; }
    public string SelectedPort { get => _selectedPort; set { _selectedPort = value; OnChanged(); } }
    public bool HardwareConfirmed { get => _hardwareConfirmed; set { _hardwareConfirmed = value; OnChanged(); } }
    public double SelectedRate
    {
        get => _selectedRate;
        set
        {
            _selectedRate = value;
            if (value > MaximumManualRate) MaximumManualRate = value;
            OnChanged();
        }
    }
    public MountProfileOption SelectedMount { get => _selectedMount; set { _selectedMount = value; OnChanged(); } }
    public string RateText => $"{SelectedRate:0.###}°/s";
    public string ConnectionAction => Snapshot.Connected ? "DISCONNECT" : Snapshot.Connecting ? "CONNECTING…" : "CONNECT";
    public string ConnectionBadge => Snapshot.Connected ? (Snapshot.ConnectionText == "Simulator" ? "SIMULATOR" : "HARDWARE LIVE") : "OFFLINE";
    public IBrush ConnectionBrush => new SolidColorBrush(Color.Parse(Snapshot.Connected ? Snapshot.ConnectionText == "Simulator" ? "#60A5FA" : "#34D399" : "#64748B"));
    public string MotionText => Snapshot.Motion switch { MotionKind.GoTo => "GOTO IN PROGRESS", MotionKind.Manual => "MANUAL SLEW", MotionKind.Tracking => $"{Snapshot.TrackingRate.ToString().ToUpperInvariant()} TRACKING", MotionKind.Park => "PARKING", MotionKind.PulseGuide => "PULSE GUIDING", _ => Snapshot.AtPark ? "PARKED" : "MOUNT IDLE" };
    public string AzimuthText => Angle.Degrees(Snapshot.Azimuth);
    public string AltitudeText => Angle.Degrees(Snapshot.Altitude);
    public string RightAscensionText => Angle.Hours(Snapshot.RightAscension);
    public string LocalSiderealTimeText => Angle.Hours(Astronomy.SiderealTimeHours(DateTimeOffset.UtcNow, _engine.Settings.Site.Longitude));
    public string DeclinationText => Angle.Degrees(Snapshot.Declination, true);
    public string FirmwareText => Snapshot.Firmware;
    public string StatusText => Snapshot.LastError ?? Snapshot.StatusMessage;
    public bool HasError => UiError != null || Snapshot.LastError != null;
    public string FullErrorText => UiError ?? Snapshot.LastError ?? "";
    public string AlignmentPointText => $"{Snapshot.AlignmentPointCount} alignment point(s)";
    public string AlignmentCoordinatesText => Snapshot.AlignmentPoints.Count == 0
        ? "No celestial alignment coordinates stored."
        : string.Join("\n\n", Snapshot.AlignmentPoints.Select((point, index) =>
            $"P{index + 1} · CELESTIAL AT SYNC\n" +
            $"      RA  {Angle.Hours(point.RightAscension)}\n" +
            $"      DEC {Angle.Degrees(point.Declination, true)}"));
    public string AlignmentStatus => Snapshot.AlignmentMode == AlignmentMode.SinglePoint
        ? $"Single-point · {Snapshot.AlignmentPointCount} point(s) · each Sync replaces the point"
        : Snapshot.AlignmentModelActive
            ? $"Multi-point · {Snapshot.AlignmentPointCount} points · 3D model active · RMS {Snapshot.AlignmentResidualDegrees:0.000}°"
            : $"Multi-point · {Snapshot.AlignmentPointCount} point(s) · three separated points required; single-point fallback until active";
    public string TrackingTitle => "Track Rate";
    public string TrackingStatus => Snapshot.Tracking ? Snapshot.TrackingRate.ToString().ToUpperInvariant() : "STOP";
    public string TrackingDetail => Snapshot.Tracking ? "Holding the target with variable AZ rates and position feedback" : "Select sidereal, lunar, or solar tracking";
    public string ParkStatus => Snapshot.AtPark ? "PARKED · Unpark required before motion" : Snapshot.ParkInProgress ? "PARKING · Stop aborts the park slew" : "NOT PARKED";
    public string PulseStatus => Snapshot.IsPulseGuiding ? "PULSE ACTIVE · tracking resumes on completion" : Snapshot.Tracking ? "Tracking active · ready for a timed correction" : "Start tracking before pulse guiding";
    public string AlpacaEndpoint => _alpaca.IsRunning ? _alpaca.Endpoint : $"http://127.0.0.1:{AlpacaPort}";
    public string AlpacaStatus => _alpaca.IsRunning ? "Listening on this PC" : "Not running";
    public string AlpacaDevicePath => "/api/v1/telescope/0";
    public string AlpacaDeviceName => $"AzPilot {SelectedMount.Name} · telescope 0";

    public string TargetAzimuth { get; set; } = "90.0";
    public string TargetAltitude { get; set; } = "45.0";
    public string TargetRightAscension { get; set; } = "12.0";
    public string TargetDeclination { get; set; } = "20.0";
    public decimal MinimumAltitude { get; set; }
    public double MaximumManualRate { get; set; }
    public double GoToRate { get; set; }
    public decimal GuideRateAzimuth { get; set; }
    public decimal GuideRateAltitude { get; set; }
    public decimal PulseDurationMs { get; set; } = 500;
    public bool AutoReconnect { get; set; }
    public bool AlpacaEnabled { get; set; }
    public decimal AlpacaPort { get; set; }
    public decimal SiteLatitude { get; set; }
    public decimal SiteLongitude { get; set; }
    public decimal SiteElevation { get; set; }
    public bool NightVision
    {
        get => _nightVision;
        set { if (_nightVision == value) return; _nightVision = value; OnChanged(); }
    }
    public AlignmentMode AlignmentMode { get; set; }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand CancelGoToCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand GoToAltAzCommand { get; }
    public AsyncRelayCommand GoToEquatorialCommand { get; }
    public AsyncRelayCommand SyncAltAzCommand { get; }
    public AsyncRelayCommand SyncEquatorialCommand { get; }
    public AsyncRelayCommand SetHomeCommand { get; }
    public AsyncRelayCommand ClearAlignmentCommand { get; }
    public AsyncRelayCommand SiderealTrackingCommand { get; }
    public AsyncRelayCommand LunarTrackingCommand { get; }
    public AsyncRelayCommand SolarTrackingCommand { get; }
    public AsyncRelayCommand StopTrackingCommand { get; }
    public AsyncRelayCommand SetParkCommand { get; }
    public AsyncRelayCommand ParkCommand { get; }
    public AsyncRelayCommand UnparkCommand { get; }
    public AsyncRelayCommand PulseUpCommand { get; }
    public AsyncRelayCommand PulseDownCommand { get; }
    public AsyncRelayCommand PulseRightCommand { get; }
    public AsyncRelayCommand PulseLeftCommand { get; }
    public AsyncRelayCommand SaveGuideRatesCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand OpenAlpacaCommand { get; }
    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowAlignCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowAscomCommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }

    private async Task ConnectDisconnect()
    {
        await Run(async () =>
        {
            if (Snapshot.Connected) { await _engine.DisconnectAsync(); return; }
            SaveSettings();
            await _engine.ConnectAsync(IsSimulator ? ConnectionKind.Simulator : ConnectionKind.Hardware, SelectedPort);
        });
    }

    public async Task BeginMove(MountAxis axis, int direction)
    {
        await Run(() =>
        {
            // Pending limits, rates, site and profile values apply before the first motor command.
            SaveSettings();
            return _engine.MoveAxisAsync(axis, SelectedRate * direction);
        });
    }
    public async Task EndMove(MountAxis axis) => await Run(() => _engine.StopAxisAsync(axis));
    public async Task EmergencyStop() => await Run(() => _engine.StopAllAsync("Keyboard / emergency stop"));

    private async Task StartTracking(TrackingRateMode rate)
    {
        await Run(async () =>
        {
            SaveSettings();
            await _engine.SetTrackingRateAsync(rate);
            await _engine.SetTrackingAsync(true);
        });
    }

    private async Task Pulse(GuideDirection direction)
    {
        await Run(async () =>
        {
            SaveSettings();
            _engine.SetGuideRates((double)GuideRateAzimuth, (double)GuideRateAltitude);
            await _engine.PulseGuideAsync(direction, (int)PulseDurationMs);
        });
    }

    private async Task Run(Func<Task> action)
    {
        try { UiError = null; await action(); }
        catch (Exception ex) { UiError = ex.Message; }
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            Mount = SelectedMount.Profile,
            ConnectionKind = IsSimulator ? ConnectionKind.Simulator : ConnectionKind.Hardware,
            PortName = SelectedPort,
            BaudRate = 115200,
            HardwareMovementConfirmed = HardwareConfirmed,
            SelectedRate = SelectedRate,
            MinimumAltitude = (double)MinimumAltitude,
            MaximumManualRate = MaximumManualRate,
            GoToRate = GoToRate,
            TrackingRate = _engine.Settings.TrackingRate,
            AlignmentMode = AlignmentMode,
            GuideRateAzimuth = (double)GuideRateAzimuth,
            GuideRateAltitude = (double)GuideRateAltitude,
            AutoReconnect = AutoReconnect,
            AlpacaEnabled = AlpacaEnabled,
            AlpacaPort = (int)AlpacaPort,
            NightVision = NightVision,
            Site = new((double)SiteLatitude, (double)SiteLongitude, (double)SiteElevation)
        };
        _engine.UpdateSettings(settings); _store.Save(settings);
    }

    private void RefreshPorts()
    {
        PortNames.Clear();
        foreach (var port in SerialTransport.GetPortNames()) PortNames.Add(port);
        if (!PortNames.Contains("SIM")) PortNames.Add("SIM");
        if (string.IsNullOrWhiteSpace(SelectedPort)) SelectedPort = PortNames.FirstOrDefault(x => x != "SIM") ?? "COM3";
    }

    private static double Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException($"'{text}' is not a valid decimal number. Use a dot for decimals.");

    private void OnChanged([CallerMemberName] string? _ = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try { SaveSettings(); } catch { }
        await _alpaca.DisposeAsync(); await _engine.DisposeAsync();
    }
}
