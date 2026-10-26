using System.Net.Http.Json;
using System.Text.Json;
using AzPilot.Alpaca;
using AzPilot.Core;
using Xunit;

namespace AzPilot.Tests;

public class AlpacaTests
{
    [Fact]
    public async Task TelescopeApi_ControlsSimulatorAndReportsStandardResponses()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        var versions = await client.GetFromJsonAsync<JsonElement>("/management/apiversions");
        Assert.Equal(1, versions.GetProperty("Value")[0].GetInt32());
        var devices = await client.GetFromJsonAsync<JsonElement>("/management/v1/configureddevices");
        Assert.Equal("Telescope", devices.GetProperty("Value")[0].GetProperty("DeviceType").GetString());

        var name = await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/name?ClientTransactionID=42");
        Assert.Equal("AzPilot Sky-Watcher AZ-EQ6", name.GetProperty("Value").GetString());
        Assert.Equal(42, name.GetProperty("ClientTransactionID").GetInt32());
        Assert.Equal(0, name.GetProperty("ErrorNumber").GetInt32());

        var slew = await Put(client, "slewtoaltazasync", new() { ["Azimuth"] = "2", ["Altitude"] = "3", ["ClientTransactionID"] = "77" });
        Assert.Equal(0, slew.GetProperty("ErrorNumber").GetInt32());
        Assert.Equal(77, slew.GetProperty("ClientTransactionID").GetInt32());
        await Task.Delay(300);
        var slewing = await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/slewing");
        Assert.True(slewing.GetProperty("Value").GetBoolean());
        var stop = await Put(client, "abortslew", new());
        Assert.Equal(0, stop.GetProperty("ErrorNumber").GetInt32());
        await Task.Delay(300);
        Assert.False(engine.Snapshot.Slewing);

        var tracking = await Put(client, "tracking", new() { ["Tracking"] = "true" });
        Assert.Equal(0, tracking.GetProperty("ErrorNumber").GetInt32());
        Assert.True(engine.Snapshot.Tracking);
        await Put(client, "abortslew", new());
    }

    [Fact]
    public async Task ClientDisconnect_DoesNotDisconnectMountOrStopTracking()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await engine.SetTrackingAsync(true);
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };

        Assert.False(await Bool(client, "connected"));
        await Put(client, "connect", new());
        Assert.True(await Bool(client, "connected"));
        await Put(client, "disconnect", new());
        Assert.False(await Bool(client, "connected"));
        Assert.True(engine.Snapshot.Connected);
        Assert.True(engine.Snapshot.Tracking);

        await Put(client, "connected", new() { ["Connected"] = "true" });
        await Put(client, "connected", new() { ["Connected"] = "false" });
        await Task.Delay(400);
        Assert.True(engine.Snapshot.Connected);
        Assert.True(engine.Snapshot.Tracking);
        Assert.False(engine.Snapshot.Slewing);
    }

    [Fact]
    public async Task PhysicalDisconnectAndReconnect_DoNotClearLogicalClientSession()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "connect", new());
        Assert.True(await Bool(client, "connected"));

        await engine.DisconnectAsync();
        Assert.False(engine.Snapshot.Connected);
        Assert.True(await Bool(client, "connected"));
        var unavailable = await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/altitude");
        Assert.Equal(1031, unavailable.GetProperty("ErrorNumber").GetInt32());

        await Put(client, "connect", new());
        Assert.True(engine.Snapshot.Connected);
        Assert.True(await Bool(client, "connected"));
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/altitude"))!.GetProperty("ErrorNumber").GetInt32());
    }

    [Fact]
    public async Task TrackingRates_ExposeSiderealLunarAndSolar()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var rates = await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/trackingrates");
        Assert.Equal(new[] { 0, 1, 2 }, rates.GetProperty("Value").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        await Put(client, "trackingrate", new() { ["TrackingRate"] = "1" });
        Assert.Equal(1, await Value<int>(client, "trackingrate"));
        Assert.Equal(TrackingRateMode.Lunar, engine.Snapshot.TrackingRate);
        await Put(client, "tracking", new() { ["Tracking"] = "true" });
        await Put(client, "trackingrate", new() { ["TrackingRate"] = "2" });
        Assert.True(engine.Snapshot.Tracking);
        Assert.Equal(TrackingRateMode.Solar, engine.Snapshot.TrackingRate);
    }

    [Fact]
    public async Task UnsafeTrackingStart_ReturnsErrorWithoutLeavingTrackingActive()
    {
        await using var engine = new MountEngine(new AppSettings { MinimumAltitude = 10 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 9);
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var response = await Put(client, "tracking", new() { ["Tracking"] = "true" });
        Assert.NotEqual(0, response.GetProperty("ErrorNumber").GetInt32());
        Assert.Contains("altitude safety limit", response.GetProperty("ErrorMessage").GetString());
        Assert.False(await Bool(client, "tracking"));
        Assert.False(await Bool(client, "slewing"));
        Assert.False(engine.Snapshot.Tracking);
        Assert.True(engine.Snapshot.Connected);
    }

    [Fact]
    public async Task InvalidTarget_ReturnsAscomInvalidValue()
    {
        await using var engine = new MountEngine(new AppSettings { MinimumAltitude = 5 });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var response = await Put(client, "slewtoaltazasync", new() { ["Azimuth"] = "20", ["Altitude"] = "-20" });
        Assert.Equal(1025, response.GetProperty("ErrorNumber").GetInt32());
        Assert.False(engine.Snapshot.Slewing);
        Assert.Contains(engine.RecentLogs, line => line.Contains("ASCOM PUT slewtoaltazasync ERROR 1025"));
    }

    private static async Task<T> Value<T>(HttpClient client, string path)
    {
        var value = await client.GetFromJsonAsync<JsonElement>($"/api/v1/telescope/0/{path}");
        return value.GetProperty("Value").Deserialize<T>()!;
    }
    private static Task<bool> Bool(HttpClient client, string path) => Value<bool>(client, path);

    private static async Task<JsonElement> Put(HttpClient client, string method, Dictionary<string, string> values)
    {
        var response = await client.PutAsync($"/api/v1/telescope/0/{method}", new FormUrlEncodedContent(values));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }
}

public class MountProfileAlpacaTests
{
    [Fact]
    public async Task SelectedMountProfileIsUsedAsTheAlpacaDeviceName()
    {
        await using var engine = new MountEngine(new AppSettings { Mount = MountProfile.SynScanDobsonian });
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/name");
        Assert.Contains("Dobsonian", response.GetProperty("Value").GetString());
    }
}

public class ParkPulseAlpacaTests
{
    [Fact]
    public async Task ParkUnparkAndParkedRejectionWorkThroughAlpaca()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "setpark", new());
        await Put(client, "slewtoaltazasync", new() { ["Azimuth"] = "2", ["Altitude"] = "3" });
        for (var i = 0; i < 150 && engine.Snapshot.Slewing; i++) await Task.Delay(100);
        await Put(client, "park", new());
        Assert.True(await Bool(client, "atpark"));
        Assert.True(await Bool(client, "canpark"));
        Assert.True(await Bool(client, "canunpark"));
        Assert.True(await Bool(client, "cansetpark"));
        var rejected = await client.PutAsync("/api/v1/telescope/0/pulseguide", new FormUrlEncodedContent(new Dictionary<string, string> { ["Direction"] = "0", ["Duration"] = "100" }));
        rejected.EnsureSuccessStatusCode();
        var rejectedBody = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1024, rejectedBody.GetProperty("ErrorNumber").GetInt32());
        await Put(client, "unpark", new());
        Assert.False(await Bool(client, "atpark"));
    }

    [Fact]
    public async Task SyncToCoordinatesWorksWhileTrackingThroughAlpaca()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "tracking", new() { ["Tracking"] = "true" });
        var ra = await Value<double>(client, "rightascension") + 0.05;
        var dec = await Value<double>(client, "declination") + 0.5;
        var response = await client.PutAsync("/api/v1/telescope/0/synctocoordinates", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RightAscension"] = ra.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Declination"] = dec.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("ErrorNumber").GetInt32());
        await Task.Delay(500);
        Assert.True(await Bool(client, "tracking"));
        Assert.False(await Bool(client, "slewing"));
        Assert.True(Math.Abs(ra - await Value<double>(client, "rightascension")) < 0.0002);
        Assert.True(Math.Abs(dec - await Value<double>(client, "declination")) < 0.001);
    }

    [Fact]
    public async Task PulseGuideAndConfigurableRatesWorkThroughAlpaca()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "tracking", new() { ["Tracking"] = "true" });
        await Put(client, "guideratedeclination", new() { ["GuideRateAltitude"] = "0.003" });
        await Put(client, "guideraterightascension", new() { ["GuideRateAzimuth"] = "0.004" });
        Assert.Equal(0.003, await Value<double>(client, "guideratedeclination"), 6);
        Assert.Equal(0.004, await Value<double>(client, "guideraterightascension"), 6);
        Assert.True(await Bool(client, "canpulseguide"));
        Assert.True(await Bool(client, "cansetguiderates"));
        await Put(client, "pulseguide", new() { ["Direction"] = "Up", ["Duration"] = "1000" });
        Assert.True(await Bool(client, "ispulseguiding"));
        Assert.False(await Bool(client, "slewing"));
        for (var i = 0; i < 50 && await Bool(client, "ispulseguiding"); i++) await Task.Delay(100);
        Assert.False(await Bool(client, "ispulseguiding"));
        Assert.True(await Bool(client, "tracking"));
    }

    [Fact]
    public async Task ReplacementPulseAndManualMoveAreAcceptedThroughAlpaca()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "tracking", new() { ["Tracking"] = "true" });
        await Put(client, "pulseguide", new() { ["Direction"] = "Up", ["Duration"] = "400" });
        await Task.Delay(75);
        await Put(client, "pulseguide", new() { ["Direction"] = "Right", ["Duration"] = "1200" });
        await Task.Delay(500);
        Assert.True(await Bool(client, "ispulseguiding"), "The old pulse timer completed its replacement.");

        await Put(client, "moveaxis", new() { ["Axis"] = "1", ["Rate"] = "0.05" });
        Assert.True(await Bool(client, "tracking"));
        Assert.True(await Bool(client, "ispulseguiding"));
        Assert.True(await Bool(client, "slewing"));
        await Put(client, "moveaxis", new() { ["Axis"] = "1", ["Rate"] = "0" });
        Assert.True(await Bool(client, "tracking"));
        Assert.True(await Bool(client, "ispulseguiding"));
        Assert.False(await Bool(client, "slewing"));
        for (var i = 0; i < 50 && await Bool(client, "ispulseguiding"); i++) await Task.Delay(100);
        Assert.False(await Bool(client, "ispulseguiding"));
        Assert.Contains(engine.RecentLogs, line => line.Contains("ASCOM PUT pulseguide OK"));
    }

    [Fact]
    public async Task PropertyReadsRemainResponsiveDuringPulseCommands()
    {
        await using var engine = new MountEngine(new AppSettings());
        await engine.ConnectAsync(ConnectionKind.Simulator, "SIM");
        await engine.SyncToAltAzAsync(180, 45);
        await using var server = new AlpacaServer(engine);
        await server.StartAsync(0, false);
        using var client = new HttpClient { BaseAddress = new Uri(server.Endpoint) };
        await Put(client, "tracking", new() { ["Tracking"] = "true" });
        await Put(client, "pulseguide", new() { ["Direction"] = "Left", ["Duration"] = "2000" });

        var reads = Enumerable.Range(0, 16).Select(_ =>
            client.GetFromJsonAsync<JsonElement>("/api/v1/telescope/0/rightascension"));
        var responses = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(responses, response => Assert.Equal(0, response.GetProperty("ErrorNumber").GetInt32()));
        Assert.True(await Bool(client, "ispulseguiding"));
        Assert.True(await Bool(client, "tracking"));
    }

    private static async Task<T> Value<T>(HttpClient client, string path)
    {
        var value = await client.GetFromJsonAsync<JsonElement>($"/api/v1/telescope/0/{path}");
        return value.GetProperty("Value").Deserialize<T>()!;
    }
    private static Task<bool> Bool(HttpClient client, string path) => Value<bool>(client, path);
    private static async Task Put(HttpClient client, string method, Dictionary<string, string> values)
    {
        var response = await client.PutAsync($"/api/v1/telescope/0/{method}", new FormUrlEncodedContent(values));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("ErrorNumber").GetInt32());
    }
}
