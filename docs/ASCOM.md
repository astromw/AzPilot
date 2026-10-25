# ASCOM Alpaca setup

AzPilot runs a local ASCOM Alpaca telescope server while the desktop app is open. The default base URL is `http://127.0.0.1:11111`; the default device is `AzPilot Sky-Watcher AZ-EQ6`, telescope number `0`; the name follows the selected mount profile, interface `ITelescopeV4`.

1. Install a current ASCOM Platform and/or an Alpaca-capable astronomy client on the same PC.
2. Start AzPilot, configure the site, and connect the simulator or mount in AzPilot.
3. In the client, choose **Alpaca** / **Alpaca Discovery** as the telescope connection type. If manual entry is needed, use host `127.0.0.1`, port `11111`, device type `telescope`, device `0`.
4. Connect from the client and verify that Alt/Az and RA/Dec match AzPilot.
5. Test `AbortSlew` before a long slew.

Useful local pages:

- Setup: `http://127.0.0.1:11111/setup`
- Management: `http://127.0.0.1:11111/management/v1/configureddevices`
- Device example: `http://127.0.0.1:11111/api/v1/telescope/0/name`

Supported operations include asynchronous Alt/Az and RA/Dec GoTo, MoveAxis, AbortSlew, single-point or multi-point sync, sidereal/lunar/solar tracking, target coordinates, site coordinates, park, and pulse guide. Synchronous legacy slews, FindHome and custom actions are not implemented. Clients requiring classic COM-only drivers cannot use this preview directly; use an Alpaca-aware path.

## Client and mount connections are independent

`Connected = true`, `Connect`, and `Disconnect` manage the ASCOM client session. Disconnecting a client, setting `Connected = false`, or closing that client does not stop motion or tracking and does not disconnect the mount from AzPilot. Before disconnecting a client during motion, deliberately use `AbortSlew` if a stop is required. The desktop **Disconnect** button closes the physical connection without stopping the mount; its last commanded motion and displayed tracking state are preserved. Use **STOP BOTH AXES** or ASCOM **AbortSlew** when a stop is required.

## Tracking rates

`TrackingRates` exposes 0=Sidereal, 1=Lunar and 2=Solar, and `TrackingRate` selects among them. Changing the rate while tracking retains the current coordinate and keeps the mount connected. Sidereal holds fixed RA/Dec. Lunar and solar use average RA-only motion of 13.176396°/day and 0.985647°/day respectively, with declination held constant; they are not exact ephemerides. All modes use variable Alt/Az motor rates with measured-position feedback.

If tracking was active before `MoveAxis` or an asynchronous GoTo, normal manual completion or GoTo completion recaptures the current sky coordinate and resumes the selected tracking rate. The desktop centre-pad **STOP** cancels a GoTo and resumes the previous rate. `AbortSlew`, **STOP BOTH AXES**, park, communication recovery and altitude-limit stops cancel automatic resumption; intentional desktop disconnect preserves the last state. A tracking request that would violate the altitude limit returns an error and leaves `Tracking = false`, `Slewing = false`, and both axes stopped.

## Sync while tracking

`SyncToAltAz`, `SyncToCoordinates` and `SyncToTarget` are accepted while tracking is active. Sync updates the selected pointing model without issuing a stop, then continues tracking the newly synced sky coordinate. Manual and GoTo motion must still be stopped before sync; parked mounts and active pulse operations remain protected.

Choose **SinglePoint** or **MultiPoint** in the desktop Alignment / Sync panel. In multi-point mode, every ASCOM or desktop Sync adds or refines a session alignment point. The 3D model activates with at least three points separated by 10° or more and is then used for position reports and GoTo inverse transformation. Use the desktop panel to inspect each point's sync-time celestial RA/Dec, point count and RMS, or clear the model. Alt/Az Sync captures converted RA/Dec at sync time; RA/Dec Sync stores the entered values directly. Clearing removes the alignment effect and restores the raw mechanical reference.

## Park

- `SetPark` saves the current mechanical position for this application session.
- `Park` waits for the slew to finish and then reports `AtPark = true`. Direction-aware altitude enforcement permits parking toward safety from an out-of-range starting altitude. `AbortSlew` cancels parking.
- `Unpark` clears the parked state. Motion, sync, tracking and pulse guide are rejected while parked.
- `CanPark`, `CanUnpark` and `CanSetPark` are true. `AtHome` and `CanFindHome` remain false.

## Pulse guiding

- `CanPulseGuide` and `CanSetGuideRates` are true. The ASCOM `GuideRateRightAscension` property controls the azimuth pulse rate, and `GuideRateDeclination` controls the altitude pulse rate. Both use degrees per second.
- `PulseGuide` uses direct Alt/Az directional semantics: 0=Up, 1=Down, 2=Right, 3=Left, with a duration of 10–30000 ms. This intentionally does not convert ASCOM N/E/S/W into equatorial corrections. It returns immediately; poll `IsPulseGuiding` for completion.
- Tracking must already be enabled. Up/Down correct altitude only; Left/Right correct azimuth only. `Slewing` remains false during a pulse, and tracking resumes at the corrected position afterward.
- Overlapping pulses and pulses while parked are rejected. `AbortSlew` cancels the pulse and stops tracking.


The server is bound to loopback and is not accessible from other devices. UDP discovery uses the standard Alpaca port 32227. If another Alpaca server uses the HTTP port, change AzPilot's port and restart.
