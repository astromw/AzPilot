# Windows startup troubleshooting

AzPilot 0.3.8 uses a normal self-contained folder rather than a compressed single-file executable. Keep every file from the package in the same writable folder; do not copy only `AzPilot.exe` to the desktop.

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
