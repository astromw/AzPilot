using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AzPilot.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace AzPilot.Alpaca;

public sealed class AlpacaServer : IAsyncDisposable
{
    private readonly MountEngine _engine;
    private WebApplication? _app;
    private CancellationTokenSource? _discoveryLifetime;
    private Task? _discoveryTask;
    private int _serverTransaction;
    private volatile bool _ascomClientConnected;
    public int Port { get; private set; }
    public bool IsRunning => _app != null;
    public string Endpoint => $"http://127.0.0.1:{Port}";

    public AlpacaServer(MountEngine engine) => _engine = engine;

    public async Task StartAsync(int requestedPort = 11111, bool discovery = true)
    {
        if (_app != null) return;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = null; options.SerializerOptions.DictionaryKeyPolicy = null; });
        builder.WebHost.UseUrls($"http://127.0.0.1:{requestedPort}");
        _app = builder.Build();
        MapEndpoints(_app);
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
        if (discovery) StartDiscovery();
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/management/apiversions", () => Results.Json(new { Value = new[] { 1 } }));
        app.MapGet("/management/v1/description", () => Results.Json(new { Value = new {
            ServerName = "AzPilot", Manufacturer = "AzPilot open-source project", ManufacturerVersion = "0.3.9", Location = "Local Windows PC" } }));
        app.MapGet("/management/v1/configureddevices", () => Results.Json(new { Value = new[] { new {
            DeviceName = $"AzPilot {MountCatalog.Get(_engine.Settings.Mount).Name}", DeviceType = "Telescope", DeviceNumber = 0,
            UniqueID = "azpilot-azeq6-local-telescope-0" } } }));
        app.MapGet("/setup", () => Results.Content(SetupHtml(), "text/html"));
        app.MapGet("/setup/v1/telescope/0/setup", () => Results.Content(SetupHtml(), "text/html"));

        // ASCOM client lifetime is independent of a transient physical USB reconnect.
        // Mount-dependent properties still return 1031 while the hardware is unavailable.
        Get(app, "connected", _ => Task.FromResult<object?>(_ascomClientConnected));
        Get(app, "connecting", _ => Task.FromResult<object?>(_engine.Snapshot.Connecting));
        Get(app, "description", _ => Task.FromResult<object?>($"Alt/Az controller for {MountCatalog.Get(_engine.Settings.Mount).Name} with direct USB motor control"));
        Get(app, "driverinfo", _ => Task.FromResult<object?>("AzPilot independent AZ driver. Simulator and direct USB. No EQ-mode operations."));
        Get(app, "driverversion", _ => Task.FromResult<object?>("0.3.9"));
        Get(app, "interfaceversion", _ => Task.FromResult<object?>((short)4));
        Get(app, "name", _ => Task.FromResult<object?>($"AzPilot {MountCatalog.Get(_engine.Settings.Mount).Name}"));
        Get(app, "supportedactions", _ => Task.FromResult<object?>(Array.Empty<string>()));
        Get(app, "alignmentmode", _ => Task.FromResult<object?>(0));
        Get(app, "altitude", _ => Task.FromResult<object?>(Required().Altitude));
        Get(app, "azimuth", _ => Task.FromResult<object?>(Required().Azimuth));
        Get(app, "declination", _ => Task.FromResult<object?>(Required().Declination));
        Get(app, "rightascension", _ => Task.FromResult<object?>(Required().RightAscension));
        Get(app, "siderealtime", _ => Task.FromResult<object?>(Astronomy.SiderealTimeHours(DateTimeOffset.UtcNow, _engine.Settings.Site.Longitude)));
        Get(app, "athome", _ => Task.FromResult<object?>(false));
        Get(app, "atpark", _ => Task.FromResult<object?>(Required().AtPark));
        Get(app, "canfindhome", _ => Task.FromResult<object?>(false));
        Get(app, "canpark", _ => Task.FromResult<object?>(true));
        Get(app, "canunpark", _ => Task.FromResult<object?>(true));
        Get(app, "canpulseguide", _ => Task.FromResult<object?>(true));
        Get(app, "cansetdeclinationrate", _ => Task.FromResult<object?>(false));
        Get(app, "cansetguiderates", _ => Task.FromResult<object?>(true));
        Get(app, "cansetpark", _ => Task.FromResult<object?>(true));
        Get(app, "cansetpierside", _ => Task.FromResult<object?>(false));
        Get(app, "cansetrightascensionrate", _ => Task.FromResult<object?>(false));
        Get(app, "cansettracking", _ => Task.FromResult<object?>(true));
        Get(app, "canslew", _ => Task.FromResult<object?>(false));
        Get(app, "canslewaltaz", _ => Task.FromResult<object?>(false));
        Get(app, "canslewasync", _ => Task.FromResult<object?>(true));
        Get(app, "canslewaltazasync", _ => Task.FromResult<object?>(true));
        Get(app, "cansync", _ => Task.FromResult<object?>(true));
        Get(app, "cansyncaltaz", _ => Task.FromResult<object?>(true));
        Get(app, "doesrefraction", _ => Task.FromResult<object?>(false));
        Get(app, "equatorialsystem", _ => Task.FromResult<object?>(1));
        Get(app, "guideratedeclination", _ => Task.FromResult<object?>(_engine.Settings.GuideRateAltitude));
        Get(app, "guideraterightascension", _ => Task.FromResult<object?>(_engine.Settings.GuideRateAzimuth));
        Get(app, "ispulseguiding", _ => Task.FromResult<object?>(Required().IsPulseGuiding));
        Get(app, "declinationrate", _ => Task.FromResult<object?>(0.0));
        Get(app, "rightascensionrate", _ => Task.FromResult<object?>(0.0));
        Get(app, "slewing", _ => Task.FromResult<object?>(Required().Slewing));
        Get(app, "slewsettletime", _ => Task.FromResult<object?>(0));
        Get(app, "targetdeclination", _ => Task.FromResult<object?>(_engine.TargetDeclination));
        Get(app, "targetrightascension", _ => Task.FromResult<object?>(_engine.TargetRightAscension));
        Get(app, "tracking", _ => Task.FromResult<object?>(Required().Tracking));
        Get(app, "trackingrate", _ => Task.FromResult<object?>((int)_engine.Snapshot.TrackingRate));
        Get(app, "trackingrates", _ => Task.FromResult<object?>(new[] { 0, 1, 2 }));
        Get(app, "utcdate", _ => Task.FromResult<object?>(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
        Get(app, "siteelevation", _ => Task.FromResult<object?>(_engine.Settings.Site.Elevation));
        Get(app, "sitelatitude", _ => Task.FromResult<object?>(_engine.Settings.Site.Latitude));
        Get(app, "sitelongitude", _ => Task.FromResult<object?>(_engine.Settings.Site.Longitude));
        Get(app, "canmoveaxis", p => Task.FromResult<object?>(p.Int("Axis") is 0 or 1));
        Get(app, "sideofpier", _ => Task.FromResult<object?>(-1));
        Get(app, "destinationsideofpier", _ => Task.FromResult<object?>(-1));

        Put(app, "action", _ => throw Error(1024, "No custom actions are supported."));
        Put(app, "connected", async p => {
            if (p.Bool("Connected")) await ConnectClient();
            else _ascomClientConnected = false;
            return null; });
        Put(app, "connect", async _ => { await ConnectClient(); return null; });
        Put(app, "disconnect", _ => { _ascomClientConnected = false; return Task.FromResult<object?>(null); });
        Put(app, "abortslew", async _ => { await _engine.StopAllAsync("ASCOM"); return null; });
        Put(app, "moveaxis", async p => {
            var axis = p.Int("Axis") == 0 ? MountAxis.Azimuth : p.Int("Axis") == 1 ? MountAxis.Altitude : throw Error(1025, "Axis must be 0 or 1.");
            var rate = p.Double("Rate");
            if (rate == 0) await _engine.StopAxisAsync(axis); else await _engine.MoveAxisAsync(axis, rate); return null; });
        Get(app, "axisrates", p => {
            if (p.Int("Axis") is not (0 or 1)) throw Error(1025, "Axis must be 0 or 1.");
            return Task.FromResult<object?>(new[] { new { Minimum = 0.001, Maximum = Math.Min(4, _engine.Settings.MaximumManualRate) } }); });
        Put(app, "slewtoaltazasync", async p => { await _engine.SlewToAltAzAsync(p.Double("Azimuth"), p.Double("Altitude")); return null; });
        Put(app, "slewtocoordinatesasync", async p => { await _engine.SlewToCoordinatesAsync(p.Double("RightAscension"), p.Double("Declination")); return null; });
        Put(app, "slewtotargetasync", async _ => { await _engine.SlewToCoordinatesAsync(_engine.TargetRightAscension, _engine.TargetDeclination); return null; });
        Put(app, "synctoaltaz", async p => { await _engine.SyncToAltAzAsync(p.Double("Azimuth"), p.Double("Altitude")); return null; });
        Put(app, "synctocoordinates", async p => { await _engine.SyncToCoordinatesAsync(p.Double("RightAscension"), p.Double("Declination")); return null; });
        Put(app, "synctotarget", async _ => { await _engine.SyncToCoordinatesAsync(_engine.TargetRightAscension, _engine.TargetDeclination); return null; });
        Put(app, "tracking", async p => { await _engine.SetTrackingAsync(p.Bool("Tracking")); return null; });
        Put(app, "targetrightascension", p => { SetTarget(p.Double("RightAscension"), _engine.TargetDeclination); return Task.FromResult<object?>(null); });
        Put(app, "targetdeclination", p => { SetTarget(_engine.TargetRightAscension, p.Double("Declination")); return Task.FromResult<object?>(null); });
        Put(app, "sitelatitude", p => { SetSite(latitude: p.Double("Latitude")); return Task.FromResult<object?>(null); });
        Put(app, "sitelongitude", p => { SetSite(longitude: p.Double("Longitude")); return Task.FromResult<object?>(null); });
        Put(app, "siteelevation", p => { SetSite(elevation: p.Double("Elevation")); return Task.FromResult<object?>(null); });
        Put(app, "slewsettletime", p => { if (p.Int("SlewSettleTime") != 0) throw Error(1025, "This preview supports zero settle time only."); return Task.FromResult<object?>(null); });
        Put(app, "trackingrate", async p => {
            var rate = p.Int("TrackingRate");
            if (rate is < 0 or > 2) throw Error(1025, "TrackingRate must be 0 (sidereal), 1 (lunar), or 2 (solar).");
            await _engine.SetTrackingRateAsync((TrackingRateMode)rate);
            return null; });

        Put(app, "setpark", async _ => { await _engine.SetParkAsync(); return null; });
        Put(app, "unpark", async _ => { await _engine.UnparkAsync(); return null; });
        Put(app, "park", async _ =>
        {
            await _engine.ParkAsync();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            while (_engine.Snapshot.ParkInProgress && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);
            if (!_engine.Snapshot.AtPark) throw new MountException("Park did not complete at the saved position.");
            return null;
        });
        Put(app, "pulseguide", async p =>
        {
            var directionText = p.String("Direction");
            directionText = directionText switch { "0" => "Up", "1" => "Down", "2" => "Right", "3" => "Left", _ => directionText };
            if (!Enum.TryParse<GuideDirection>(directionText, true, out var direction) || !Enum.IsDefined(direction))
                throw Error(1025, "Direction must be Up, Down, Right, Left, or numeric 0–3.");
            await _engine.PulseGuideAsync(direction, p.Int("Duration")); return null;
        });
        Put(app, "guideratedeclination", p => { _engine.SetGuideRates(_engine.Settings.GuideRateAzimuth, p.Double("GuideRateAltitude")); return Task.FromResult<object?>(null); });
        Put(app, "guideraterightascension", p => { _engine.SetGuideRates(p.Double("GuideRateAzimuth"), _engine.Settings.GuideRateAltitude); return Task.FromResult<object?>(null); });

        foreach (var name in new[] { "aperturearea", "aperturediameter", "focallength" })
            Get(app, name, _ => throw Error(1024, $"{name} is not available from the mount."));
        foreach (var name in new[] { "commandblind", "commandbool", "commandstring", "findhome", "slewtoaltaz", "slewtocoordinates", "slewtotarget", "setupdialog" })
            Put(app, name, _ => throw Error(1024, $"{name} is not implemented by this AZ-only Alpaca driver."));
    }

    private async Task ConnectClient()
    {
        if (!_engine.Snapshot.Connected)
            await _engine.ConnectAsync(_engine.Settings.ConnectionKind, _engine.Settings.PortName);
        _ascomClientConnected = true;
    }

    private void Get(WebApplication app, string name, Func<Parameters, Task<object?>> action) =>
        app.MapGet($"/api/v1/telescope/0/{name}", (Func<HttpContext, Task<IResult>>)(ctx => Handle(ctx, action)));
    private void Put(WebApplication app, string name, Func<Parameters, Task<object?>> action) =>
        app.MapPut($"/api/v1/telescope/0/{name}", (Func<HttpContext, Task<IResult>>)(ctx => Handle(ctx, action)));

    private async Task<IResult> Handle(HttpContext context, Func<Parameters, Task<object?>> action)
    {
        var clientTransaction = 0u;
        var serverTransaction = (uint)Interlocked.Increment(ref _serverTransaction);
        var timer = Stopwatch.StartNew();
        var method = context.Request.Method;
        var name = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown";
        try
        {
            var parameters = await Parameters.Read(context);
            clientTransaction = parameters.UInt("ClientTransactionID");
            var value = await action(parameters);
            // Commands are individually traceable. High-frequency property polls are logged
            // only when slow, keeping the field log useful instead of flooding it.
            if (method != "GET" || timer.ElapsedMilliseconds >= 250)
                _engine.LogDiagnostic($"ASCOM {method} {name} OK · {timer.ElapsedMilliseconds} ms");
            return Results.Json(value == null
                ? new Response(null, clientTransaction, serverTransaction, 0, "")
                : new Response(value, clientTransaction, serverTransaction, 0, ""));
        }
        catch (AlpacaError ex)
        {
            _engine.LogDiagnostic($"ASCOM {method} {name} ERROR {ex.Number} · {timer.ElapsedMilliseconds} ms · {ex.Message}");
            return Results.Json(new Response(null, clientTransaction, serverTransaction, ex.Number, ex.Message));
        }
        catch (Exception ex)
        {
            var number = ex switch { MountException when !_engine.Snapshot.Connected => 1031,
                MountParkedException or MountInvalidOperationException => 1024,
                ArgumentOutOfRangeException or FormatException => 1025, MountException => 1035, _ => 1280 };
            _engine.LogDiagnostic($"ASCOM {method} {name} ERROR {number} · {timer.ElapsedMilliseconds} ms · {ex.GetType().Name}: {ex.Message}");
            return Results.Json(new Response(null, clientTransaction, serverTransaction, number, ex.Message));
        }
    }

    private MountSnapshot Required() => _engine.Snapshot.Connected ? _engine.Snapshot : throw Error(1031, "The mount is not connected.");
    private static AlpacaError Error(int number, string message) => new(number, message);
    private void SetTarget(double ra, double dec)
    {
        if (ra is < 0 or >= 24 || dec is < -90 or > 90) throw Error(1025, "Invalid target coordinates.");
        _engine.SetTarget(ra, dec);
    }
    private void SetSite(double? latitude = null, double? longitude = null, double? elevation = null)
    {
        var site = _engine.Settings.Site;
        var updated = new SiteInfo(latitude ?? site.Latitude, longitude ?? site.Longitude, elevation ?? site.Elevation);
        if (updated.Latitude is < -90 or > 90 || updated.Longitude is < -180 or > 180 || updated.Elevation is < -300 or > 10000)
            throw Error(1025, "Observing-site value is outside the ASCOM range.");
        _engine.UpdateSettings(_engine.Settings with { Site = updated });
    }

    private void StartDiscovery()
    {
        _discoveryLifetime = new CancellationTokenSource();
        _discoveryTask = Task.Run(async () =>
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 32227));
            while (!_discoveryLifetime.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(_discoveryLifetime.Token);
                    if (Encoding.ASCII.GetString(result.Buffer) == "alpacadiscovery1")
                        await udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { AlpacaPort = Port }), result.RemoteEndPoint);
                }
                catch (OperationCanceledException) { break; } catch { await Task.Delay(250); }
            }
        });
    }

    private string SetupHtml() => $$"""
        <!doctype html><html><head><meta charset=utf-8><title>AzPilot ASCOM Setup</title>
        <style>body{max-width:720px;margin:48px auto;background:#0b1220;color:#e5edf7;font:16px system-ui}main{padding:32px;background:#131e30;border:1px solid #29384d;border-radius:16px}code{color:#7dd3fc}li{margin:10px 0}</style></head>
        <body><main><h1>AzPilot {{MountCatalog.Get(_engine.Settings.Mount).Name}}</h1><p>Independent Alt/Az controller and ASCOM Alpaca telescope server.</p>
        <ul><li>Device: <code>telescope/0</code></li><li>Interface: ITelescopeV4</li><li>Endpoint: <code>{{Endpoint}}</code></li><li>Binding: this PC only</li></ul>
        <p>Connection, port, site coordinates and safety limits are configured in the AzPilot desktop app. Close SynScan before using the same USB port.</p>
        <p><strong>Safety:</strong> keep the mount under observation and test at low speed before remote operation.</p></main></body></html>
        """;

    public async Task StopAsync()
    {
        _discoveryLifetime?.Cancel();
        if (_discoveryTask != null) try { await _discoveryTask; } catch { }
        if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); _app = null; }
    }
    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed record Response(object? Value, uint ClientTransactionID, uint ServerTransactionID, int ErrorNumber, string ErrorMessage);
    private sealed class AlpacaError(int number, string message) : Exception(message) { public int Number { get; } = number; }

    private sealed class Parameters(Dictionary<string, string> values)
    {
        private readonly Dictionary<string, string> _values = values;
        public string String(string name) => _values.TryGetValue(name, out var value) ? value :
            throw Error(1025, $"Missing parameter: {name}.");
        public double Double(string name) => double.TryParse(String(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw Error(1025, $"{name} must be a number using invariant decimal notation.");
        public int Int(string name) => int.TryParse(String(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw Error(1025, $"{name} must be an integer.");
        public uint UInt(string name) => _values.TryGetValue(name, out var value) && uint.TryParse(value, out var result) ? result : 0;
        public bool Bool(string name) => String(name).Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => throw Error(1025, $"{name} must be true or false.") };
        public static async Task<Parameters> Read(HttpContext context)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in context.Request.Query) values[pair.Key] = pair.Value.ToString();
            if (context.Request.HasFormContentType)
                foreach (var pair in await context.Request.ReadFormAsync()) values[pair.Key] = pair.Value.ToString();
            return new(values);
        }
    }
}
