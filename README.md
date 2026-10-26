# AzPilot 0.3.9 Preview

AzPilot is an independent Windows Alt/Az controller for the Sky-Watcher AZ-EQ6 and other selectable SynScan Alt/Az mount profiles. It uses the mount's built-in USB-B connection directly and includes an ASCOM Alpaca ITelescopeV4 server. It is not affiliated with or endorsed by Sky-Watcher.

**Status:** supervised hardware commissioning preview. Field testing has begun on an AZ-EQ6; 0.3.9 addresses the reported ASCOM-session and composite-motion defects, but smooth PHD2 calibration and long-duration USB reliability still require physical validation. Every listed model profile requires supervised commissioning; inclusion does not mean that model has been hardware-verified. Keep the mount under observation and use the staged checklist below before normal operation.

## Included

- Simulator and direct USB-B serial backends, 115200 baud
- AZ-EQ6, AZ-EQ5, EQ6-R (AZ mode), SynScan GoTo Dobsonian, and generic SynScan Alt/Az profiles
- Celestial-sphere all-sky display with a live telescope-pointing dot
- EQMOD-style driver panel with position readouts, uniform centred square controls, geometrically equal-length manual/pulse arrows, a dark-navy high-contrast blue palette, and the original dark palette as night vision
- Fixed bottom status/alert strips with prominent error styling; showing or clearing an alert never shifts mount controls or setup pages
- **SETUP** expands or closes the right-hand panel, with pointing/GoTo, alignment, site, ASCOM and diagnostics tabs
- Collapsed mount controls and expanded setup pages fit without scrolling at their minimum window sizes
- Press-and-hold direction pad and arrow-key slewing, with rates from 0.005 to 4°/s
- Centre-pad GoTo cancellation with previous-rate tracking resume; immediate both-axis stop with STOP BOTH AXES, Space, or Escape
- Alt/Az GoTo and topocentric RA/Dec GoTo
- Selectable single-point offset or session-based multi-point alignment with celestial RA/Dec captured at Sync and displayed for every point; three or more separated sync points fit a 3D Alt/Az transformation used by GoTo, readouts and tracking
- Icon buttons for sidereal, lunar and solar tracking, plus Stop; variable Alt/Az rates use closed-loop position feedback
- Manual arrows are additive to active tracking and pulse rates: tracking remains on, position feedback does not oppose the arrow, and final release rebases tracking at the corrected coordinate; GoTo completion and centre-pad GoTo cancellation also resume tracking
- Session park position, Park/Unpark, and parked-motion locking
- Timed Up/Down/Left/Right Alt/Az pulse guiding with independent axis rates, safe replacement of an active pulse, and manual overlays without cancelling tracking
- Configurable minimum altitude from −90° to +80°, including genuine below-horizon operation, and manual-rate limits; direction-aware enforcement allows manual and park motion back toward safety while stopping motion farther out of bounds
- Conservative slow-GoTo default and motion-stop before mode changes
- Automatic USB reconnection without automatically resuming motion
- ASCOM Alpaca management, discovery, ITelescopeV4 and local setup page; the logical client session remains connected across physical USB reconnects while mount-dependent properties correctly report physical outages
- Persistent activity/ASCOM command, error and slow-request diagnostics, plus blue daytime/night-vision palette selection
- 70 automated protocol, coordinate, alignment, simulator, profile, speed, composite tracking/manual/pulse, replacement-pulse, tracking-resume/drift, GoTo-cancel, disconnect-state, tracking-start safety, altitude-recovery, ASCOM-lifecycle/concurrency, sync, park and API tests

## Run on Windows

1. Extract `AzPilot-Windows-x64.zip` to a folder where you want to keep the app.
2. Keep `AzPilot.exe` together with all supplied DLL files and folders. Run `AzPilot.exe`; no .NET installation or single-file extraction is required.
3. The app starts **disconnected** in **Simulator** mode; nothing connects automatically. Press **Connect** to explore the controls without hardware.
4. Choose the appropriate **Mount model**. Profiles share the SynScan protocol but still require a compatible direct motor-controller serial/USB connection.
5. Manual rates range from **0.005 to 4°/s**. The three finest choices are 0.005, 0.01 and 0.025°/s; use 0.05–0.25°/s for initial hardware direction checks. Selecting a higher manual rate also raises the manual-rate ceiling, whose application maximum remains 4°/s.
6. Open **Site / Setup**, enter your observing latitude/longitude and minimum altitude. A negative value explicitly permits that many degrees below the horizon; for example, **−5°** allows GoTo and manual motion below 0° down to approximately −5°. Pending motion settings are applied before movement; **Save Settings** retains them for the next session. GoTo speed and the manual-rate ceiling are also on this tab.
7. For ASCOM, keep AzPilot running. See `docs/ASCOM.md`.

Windows may show SmartScreen for an unsigned application. The portable build is not code-signed. If startup fails, see `docs/TROUBLESHOOTING.md` for the diagnostic log location.

## First real-mount connection

**Read `docs/HARDWARE-CHECKLIST.md` before moving the mount.**

1. Configure the mount physically for **AZ mode** and select its profile. Dobsonian and generic profiles use the same direct SynScan protocol path; no hand-controller PC Direct mode is added. AzPilot does not implement EQ-mode geometry or meridian handling.
2. Level the tripod. Point the tube level toward true north before power-on if you want to use the default north/level reference.
3. Close SynScan Pro, SynScan App, ASCOM mount drivers, and any other program using the mount COM port.
4. Connect the PC to the **mount's built-in USB-B port**, not the hand-controller PC Direct connection. Select its COM port in Device Manager/AzPilot.
5. Select **Sky-Watcher / SynScan USB** and press **Connect**. There is no mandatory confirmation checkbox; completing `docs/HARDWARE-CHECKLIST.md` remains your responsibility before hardware motion.
6. Confirm that the displayed firmware and position reply are sensible. Disconnect immediately if replies are erratic.
7. Set manual rate to **0.05 or 0.25 °/s**. The 3–4°/s choices are only for later, fully supervised tests after low-speed direction and stopping are proven. Test one short movement in each direction and verify each screen direction matches the mount.
8. Test release-to-stop, both-axis STOP, Space, and Escape. Then test a very small, slow GoTo in a clear direction.
9. Test a small park slew and Unpark, then a short pulse while tracking. Confirm Stop cancels either operation.
10. Only after those tests pass, use tracking, guiding and ASCOM with short, supervised operations.

The sky display is a looking-up celestial-sphere projection: the rim is the horizon, the center is zenith, east is left and west is right. The dot follows the same orientation, so the display matches the sky as seen by an observer facing south in the northern hemisphere.

The desktop **Disconnect** button closes the serial connection without sending a stop: the mount retains its last commanded motion and AzPilot retains the last displayed tracking state. Software tracking updates cannot continue while disconnected; reconnecting starts a new software control session and does not re-enable software tracking automatically. Closing AzPilot while still connected attempts a both-axis stop. The physical power switch remains the final emergency stop. Disconnecting an ASCOM client only ends that client's session and likewise does not stop the mount. Use **STOP BOTH AXES**, Space, Escape, or the ASCOM client's **AbortSlew** when a stop is required.

## Coordinate and tracking model

- Default encoder reference is north/level: mechanical Az 0°, Alt 0°.
- **SinglePoint** applies one angular offset per axis, as before. **MultiPoint** collects sync positions and activates after at least three points with 10° or more separation. It fits a best-fit 3D rotation between mechanical encoder vectors and observed sky Alt/Az vectors, compensating rigid rotation, tripod tilt and similar fixed errors. More points refine the fit; the panel reports RMS residual. Nearby points replace earlier duplicates. The left panel lists each point's celestial RA/Dec captured at the moment of Sync; Alt/Az Sync converts using the sync-time clock and site, while RA/Dec Sync stores the entered coordinates directly. These displayed coordinates do not drift later. **Clear Points** removes the offset/model and restores the raw mechanical reference, so displayed sky coordinates change accordingly without commanding a slew; active tracking is rebased to avoid a tracking jump.
- Multi-point alignment is session-only and is shared by desktop and ASCOM sync operations. It is not plate solving and does not model time-dependent flexure, refraction or changing mechanical errors.
- RA/Dec conversion uses PC UTC time, the saved observing site, and topocentric coordinates. Precession, nutation and refraction are not modeled.
- Sidereal tracking holds the sky coordinate present when tracking is enabled. Both motor rates are updated approximately once per second using feed-forward target motion plus bounded feedback from the measured Alt/Az position, preventing small timing and rate errors from accumulating into RA/Dec drift.
- Lunar tracking advances RA at an average 13.176396°/day; solar tracking advances RA at an average 0.985647°/day. Both hold declination constant in this model. They are tracking-rate conveniences, not exact lunar/solar ephemerides; the Moon's actual rate and declination motion vary.
- The tracking-rate buttons are **★ Sidereal**, **☾ Lunar**, a drawn disc-and-rays **Solar** icon, and **■ Stop**. The adjacent status reads SIDEREAL, LUNAR, SOLAR, or STOP. Selecting a different rate while tracking retains the current coordinate and does not issue a mount disconnect.
- Manual movement no longer suspends tracking. The selected tracking rate (or active pulse rate) is combined with each held arrow rate, with a 4°/s combined motor ceiling. While an arrow is held, tracking uses feed-forward rates only so position feedback cannot pull back toward the old coordinate. Releasing the final manual axis refreshes the encoder position and holds that corrected sky coordinate. A completed GoTo does the same at its destination. The centre-pad **STOP** cancels a GoTo and resumes the previous rate; **STOP BOTH AXES**/AbortSlew, park, communication recovery and safety-limit stops intentionally cancel tracking and pending resume. Intentional desktop disconnect preserves the last state instead.
- Tracking startup is atomic: the current and next target altitude are checked before motion, and any startup failure leaves `Tracking = false` with both axes stopped. Tracking also stops if the required rate exceeds 1°/s near the zenith. Position corrections are limited to 0.05°/s per axis within that overall ceiling.
- Altitude-limit polling compares consecutive measured positions. If the current altitude is below the minimum, upward manual or park motion toward the safe range is allowed; downward motion farther below the limit is stopped. Equivalent recovery toward 90° is allowed from above 90°. Tracking startup outside the safe range remains rejected.
- Communication-failure recovery leaves all motion and tracking stopped. An intentional Disconnect is different: it leaves the mount's last commanded motion unchanged. A later user-initiated Connect starts a new software control session; inspect the mount and use STOP BOTH AXES if a stop is needed.

## Park and pulse guide

- **Set current** saves the current mechanical encoder position as the park target. Without it, park uses the initial mechanical Az 0° / Alt 0° reference. The target is retained only for the current application session.
- **Park** performs a normal slow GoTo to that position. Parking may recover from an out-of-range current altitude when the saved park position is in the safe direction. `AtPark` becomes true only after both axes stop near the target. Stop/AbortSlew cancels an active park slew.
- While parked, normal motion, GoTo, sync, tracking and pulse guide are locked. Use **Unpark** before continuing.
- Pulse guiding requires active tracking. Up/Down adjust altitude only; Left/Right adjust azimuth only. These are direct Alt/Az corrections, not equatorial N/E/S/W corrections. Rates are in degrees per second and durations in milliseconds.
- A pulse adds a correction to the selected axis while the other axis maintains its tracking rate. On completion, tracking continues at the corrected position. A new pulse replaces the active pulse and invalidates its old completion timer. Manual arrows remain available during a pulse and are added to the pulse/tracking rates; releasing them does not cancel either mode.
- Sync and tracking-rate changes are accepted during a pulse. GoTo and Park deliberately cancel the pulse before starting their own motion. Guide-rate edits affect subsequent pulses without interrupting the active one. Stop/AbortSlew cancels manual, pulse and tracking together.
- Pulse guide and park are available in the dashboard and through ASCOM Alpaca. Automatic Find Home, PEC and ST-4 control are not included. PHD2 calibration and guiding still require supervised hardware validation.

## Field diagnostics

The desktop activity log is also appended to `%LOCALAPPDATA%\AzPilot\logs\startup-YYYYMMDD.log`. ASCOM commands, returned errors, request durations, slow property reads, pulse replacement/completion, communication warnings and reconnection outcomes are recorded there. If Cartes du Ciel or PHD2 reports a failure, retain the log covering that exact attempt; it provides evidence of whether the request reached AzPilot and whether the failure came from the ASCOM operation or the mount/serial path.

0.3.9 keeps Alpaca rather than adding a classic COM driver. COM would change the client API, not the mount's USB protocol, motor-command sequence or serial timing, so it would not inherently improve guiding or response. The ASCOM `Connected` property now represents the logical client session and remains true during a physical reconnect; mount-dependent properties return ASCOM error 1031 until the physical mount is available again.

## Deliberate preview limits

- AZ mode only; no EQ mode, polar alignment, meridian flip, or pier-side logic
- No plate solving or persistent alignment points; multi-point fitting is session-only and does not model flexure or refraction
- No automatic find-home, PEC, or ST-4 guiding controls
- Park positions are session-only and are not restored as safe after a power cycle
- No classic COM ASCOM driver; integration is through ASCOM Alpaca
- Alpaca listens on `127.0.0.1` only, for clients on the same PC
- Alpaca port changes require an application restart

## Build from source

Requires .NET 8 SDK.

```powershell
dotnet restore
dotnet test
dotnet publish src/AzPilot.App/AzPilot.App.csproj -c Release -r win-x64 --self-contained true
```

The solution is divided into `AzPilot.Core` (protocol, motion, safety and transforms), `AzPilot.Alpaca` (ASCOM server), `AzPilot.App` (Avalonia desktop UI), and `AzPilot.Tests`.

## Protocol references

- Sky-Watcher Motor Controller Command Set: https://inter-static.skywatcher.com/downloads/skywatcher_motor_controller_command_set.pdf
- Sky-Watcher application-development downloads: https://skywatcher.com/download/manual/application-development/
- ASCOM Alpaca API reference: https://ascom-standards.org/AlpacaDeveloper/ASCOMAlpacaAPIReference.html
- ASCOM ITelescopeV4: https://ascom-standards.org/newdocs/telescope.html

Use of the Sky-Watcher name is descriptive only. See `THIRD-PARTY-NOTICES.md` and `LICENSE`.
