# Windows startup troubleshooting

AzPilot 0.3.9 uses a normal self-contained folder rather than a compressed single-file executable. Keep every file from the package in the same writable folder; do not copy only `AzPilot.exe` to the desktop.

## If the window does not appear

1. Extract the complete ZIP to a normal folder such as `Documents\AzPilot` or `C:\AzPilot`. Do not run from inside the ZIP preview.
2. Run `AzPilot.exe` directly. No mount or USB cable is needed. The app starts disconnected with Simulator selected; press **Connect** when you are ready.
3. If an error dialog appears, read the message and keep the diagnostic file it names.
4. Check `%LOCALAPPDATA%\AzPilot\logs\startup-YYYYMMDD.log`. The log records startup milestones and fatal exceptions.
5. If there is no log, the application did not get far enough to execute its startup code. Check Windows architecture, extraction, SmartScreen and antivirus/endpoint-protection blocks.
6. In Event Viewer, check **Windows Logs → Application** around the attempted start for **Application Error** or **.NET Runtime** entries involving `AzPilot.exe`.

`Diagnose-AzPilot.cmd` opens the log folder and displays basic Windows information. It does not change mount settings or connect to hardware.

## Supported package

The supplied package is 64-bit Windows (`win-x64`) for 64-bit Windows 10/11. It will not run on 32-bit Windows or Windows ARM. The app is unsigned, so SmartScreen or organizational security software may require an explicit allow decision.

Do not whitelist or disable security software globally. If a policy blocks the app, use the file and log details to make a narrowly scoped exception according to your organization's policy.

## Cartes du Ciel, PHD2, pulse or USB problems

1. Close SynScan and every other application that could use the same COM port. Keep the physical power switch within reach and test only under supervision.
2. Reproduce the problem with 0.3.9, noting the approximate time and whether the client reported a disconnect, a pulse error, rough motion, or a calibration failure.
3. Preserve `%LOCALAPPDATA%\AzPilot\logs\startup-YYYYMMDD.log`. In addition to startup details, it contains mount activity, ASCOM commands and durations, returned ASCOM errors, property reads slower than 250 ms, pulse replacement/completion, serial warnings and reconnect results.
4. `Connected` describes the logical ASCOM client session. It deliberately remains true during a physical USB reconnect; mount-dependent properties return error 1031 while the mount is unavailable. This separates a client-session failure from a physical connection failure.
5. A new pulse replaces an active pulse. Manual MoveAxis corrections are added to tracking/pulse rates, and tracking remains enabled. AbortSlew remains the complete stop for manual, pulse and tracking.
6. Do not assume that changing from Alpaca to classic COM will improve response. Both APIs would still depend on the same SynScan serial exchange and motor-command timing. Use the recorded request/error timing to identify the actual failing layer first.

Simulator tests cannot establish physical guiding smoothness. PHD2 calibration still needs a supervised hardware check after basic tracking, arrows, pulses, stopping and recovery have passed.
