# Simple Shift Tracker

A small, offline Windows app for tracking your work shifts and monthly earnings. Built with .NET 10, WPF, and a local SQLite database.

## Run the release

Extract **all files** from `artifacts/SimpleShiftTracker-win-x64.zip` into a folder and open **SimpleShiftTracker.exe**. The Windows x64 release includes the .NET runtime; no .NET installation, database server, account, or internet connection is required to use it.

The unpacked release is also in `artifacts/publish/win-x64/`. Keep its supporting files beside the executable.

## Track work

- **Start shift** begins paid time.
- **Pause** starts an unpaid break. **Resume** continues the same shift.
- **End shift** finishes it and makes its times editable. Ending while paused keeps the break unpaid.
- Closing the app, signing out, shutting down Windows, or sleeping the PC pauses an unfinished shift. Reopening or waking leaves it paused. Minimizing keeps it running.
- Only one shift can be unfinished at a time.

Use the month arrows or date picker to browse your history. **This month** returns to the current month. The totals show worked time to the second and estimated gross earnings. Hours do not wrap at 24.

## Correct your history

**Edit** changes a completed shift's start, end, and individual unpaid breaks. **Add shift** records forgotten work; it defaults to yesterday, or the end of the selected past month. Dates use **day/month/year** and times use **24-hour HH:mm:ss**. The preview updates before saving.

Breaks must stay inside the shift and cannot overlap. Completed shifts cannot overlap or end in the future. End an unfinished shift before editing or deleting it. Deletion asks for confirmation. Changes immediately recalculate the relevant months.

If a local time occurs twice when daylight saving ends, choose the intended UTC offset. A local time skipped when clocks move forward is rejected.

## Wages and monthly summaries

The starting rate is **€10.00 per hour**. Click the hourly-rate button to change it. Positive rates with up to two decimal places are supported, using either a dot or comma as the decimal separator.

Each shift retains the rate saved when it was created. Changing the default applies to future shifts, including newly added manual shifts. Correcting an old shift's times preserves its saved rate.

Monthly summaries are calculated automatically from your stored work, including the running shift. They appear whenever the app is open; no background scheduler is required. At a new month the view advances automatically if you were viewing the current month.

Reports use **Europe/Bratislava** calendar boundaries. Overnight shifts and breaks crossing a month are split accurately between months. Timestamps are stored in UTC, so daylight-saving changes do not distort elapsed work.

Earnings = worked seconds × saved hourly rate ÷ 3,600. Hours are never rounded for pay. Money is rounded to cents using midpoint-away-from-zero; monthly totals round the sum of unrounded earnings. Individually rounded rows can therefore differ from the rounded total by a cent. Earnings are estimates before any taxes, deductions, or overtime rules.

**Export CSV** saves the selected month's shift details, month-clipped time range, unpaid breaks, exact worked seconds formatted as hours/minutes/seconds, saved rates, and totals. Files use UTF-8 with a BOM, comma separators, quoted fields, and timestamps with UTC offsets. An unfinished shift is identified as running or paused. Export records the summary at the time you clicked Export.

## Data and recovery

Data is stored at `%LOCALAPPDATA%\SimpleShiftTracker\tracker.db`. Settings shows the full path. The app does not send your work history anywhere.

To back up, **close the app**, then copy `tracker.db` somewhere safe. To restore, close the app and replace the database with your backup. Updating the app folder does not replace your database. CSV files are reports, not restorable database backups.

State changes save immediately using database transactions. While running, the app saves a checkpoint every 15 seconds. If it crashes or is forcibly terminated, the shift is restored paused at its last checkpoint. A missed sleep event, a tracking gap over 45 seconds, or a backwards clock change also pauses it at the last checkpoint. The recovery notice tells you to review the times after ending the shift. The most recent unsaved seconds may need correction.

Storage failures show an error and preserve existing data. Diagnostic details go to `tracker.log` beside the database. Database migrations run when the app starts; an interrupted upgrade can be retried on the next launch. Back up before updating to future versions.

Optional launch arguments:

```powershell
.\SimpleShiftTracker.exe --data-dir "D:\MyTrackerData"
.\SimpleShiftTracker.exe --theme dark
.\SimpleShiftTracker.exe --theme light
```

Theme follows the Windows app setting by default. A custom data folder also allows independent test data without touching your normal work history. Only one app instance can open each data folder.

## Build from source

Requires Windows and the **.NET 10 SDK**. `global.json` starts from SDK 10.0.301 and permits newer .NET 10 feature bands.

```powershell
.\build.ps1
.\build.ps1 -Publish
dotnet run --project src/Tracker.App
```

The build script restores packages, builds Release, and runs the automated tests plus WPF UI smoke checks in both themes. The UI checks use fresh databases, exercise the real windows and dialogs, and save PNG screenshots under `.artifacts/ui-smoke/`. They do not suspend the PC; they exercise the same pause handler used for suspend/shutdown events. Use `-SkipUiSmoke` for environments that cannot create Windows UI windows. `-Publish` additionally creates a self-contained Windows x64 folder and zip. Build caches live in `.artifacts/`. Package restore needs internet on the first build.

The solution contains `Tracker.App` (WPF/MVVM), `Tracker.Core` (tracking, calculations, validation, SQLite), `Tracker.Tests` (real SQLite integration and calculation tests with an injected clock), and the development-only `Tracker.UiSmoke` harness.

For a database model change:

```powershell
dotnet tool restore
dotnet ef migrations add YourMigration --project src/Tracker.Core
```

Commit the migration, designer, and model snapshot together. SQLite's custom `IX_Shifts_OneUnfinished` expression index is created in the initial migration; preserve or recreate it when a future migration rebuilds the Shifts table. Migrations must preserve existing shifts.
