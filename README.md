# Strava Reports

A personal, local-first Strava reporting app, built from `Stravaapp_prd.md`. It has one shared core and one shared UI, hosted two ways:

| Project | What it is |
|---|---|
| `StravaReports.Core` | EF Core + SQLite model and migrations, Strava API client, OAuth/token service, rate-limit tracker, resumable sync engine, report queries, stream analysis, CSV export, and the export-archive importer. No platform code. |
| `StravaReports.Shared` | Razor class library: every page, report view, chart, the Settings page and navigation. Charts use Blazor-ApexCharts. |
| `StravaReports.Maui` | .NET MAUI Blazor Hybrid app (iOS first; Android, Windows and Mac Catalyst also build). On-device SQLite, SecureStorage (Keychain), WebAuthenticator. |
| `StravaReports.Web` | Blazor Web App (interactive server). Server-side SQLite, Data Protection for secrets, single-user login, scheduled background sync, OAuth endpoints. |
| `StravaReports.Tests` | xUnit tests for calculations, rate limits, the sync state machine (against a fake Strava API) and report queries. |

## Run it

```bash
dotnet test                                                       # 63 tests
dotnet run --project StravaReports.Web --launch-profile https     # web app
dotnet build StravaReports.Maui -t:Run -f net10.0-windows10.0.19041.0   # desktop MAUI
```

iOS: build from Visual Studio paired to a Mac, or on a Mac with `dotnet build -t:Run -f net10.0-ios`.

### First run of the web app

The site is single-user and internet-facing, so on first start it writes a one-time code to `App_Data/setup-code.txt` (the file, not the log). Open `/account/setup`, enter the code and choose a password (12+ characters). The code file is then deleted. Set `DataDirectory` (for example `/home/data` on Azure App Service) to keep the database, encrypted secrets and Data Protection keys on persistent storage.

### Connecting Strava

1. Create an API application at <https://www.strava.com/settings/api>. Set **Authorization Callback Domain** to your web app's host (`localhost` while developing).
2. In the app, open **Settings**, enter the Client ID and Client Secret, and press **Connect to Strava**.
   * **Web:** redirects to Strava and back to `/auth/strava/callback`.
   * **iOS/Android:** set **OAuth relay** to your web app's URL. Strava redirects to `/auth/strava/app-callback` on the web app, which forwards the code to `stravareports://oauth`, where WebAuthenticator picks it up. Strava only allows one callback domain, which is why this relay is needed.
   * **Windows/Mac desktop:** leave the relay empty. The app listens on `http://localhost:<port>/callback`, which Strava always allows.
3. The first sync starts straight away. Summary data arrives first, then detail (laps, best efforts, gear) fills in from a queue.

## How sync works

`SyncEngine` runs these stages, committing after every page or job:

1. Refresh the token if it expires within 10 minutes, saving the new refresh token immediately.
2. Incremental: fetch activities `after` the newest one stored.
3. Backfill: page backwards with `before`, saving a cursor after each page.
4. Reconcile: weekly, compare the last 30 days with Strava to catch deletions and edits.
5. Detail and streams queue: newest first, with retries and backoff (streams are off by default).

Every response's `X-ReadRateLimit-*` / `X-RateLimit-*` headers are tracked. Each run stops a few requests short of the 15-minute or daily limit, or on a 429, and records `ResumeAfterUtc`: the next 15-minute mark, or midnight UTC for the daily limit. The web app's background service resumes then. MAUI syncs on launch, when it returns to the foreground (if the last run is over 15 minutes old or work is waiting), and when you press **Sync now**.

The web app's scheduler (`SyncSchedule`) checks every 15 seconds and applies these rules in order:
1. A rate-limit pause wins over everything else.
2. After a failed run it backs off: 5, 10, 20 minutes and so on, up to 6 hours.
3. Webhook events trigger a run, after a 20-second debounce.
4. The backlog runs every 5 minutes.
5. Otherwise it waits for your interval. While webhooks are active, the interval stretches to a 6-hour safety-net poll.

## Webhooks (web app)

Under **Settings → Strava webhooks**, press **Subscribe**. Strava needs to reach `https://<your site>/webhooks/strava` from the internet, so this only works once the app is deployed (not on localhost). Each subscription gets a fresh random verify token, kept in the encrypted secret store.

Strava doesn't sign events, so they are handled defensively:
* Events are only stored if they match the subscription ID and your athlete ID. There's a cap of 500 pending events.
* Events are only hints. The next sync re-reads each affected activity from the API, so a forged "delete" for an activity that still exists just refreshes it.
* A deauthorization event disconnects only after the API confirms the token was revoked.
* Webhooks also catch old activities uploaded late, which the incremental pass misses.

## All data

**All data** (under All sports) is the page to explore with. The filter bar sets the date range, sport, gear, trainer, commute and archive; **Refine** adds a text search (name, sport or gear name; every word must match), distance, moving time and elevation ranges, day of week, and has-heart-rate / has-power. Everything on the page follows what is showing: totals (distance, activities, moving, elapsed and stopped time, elevation and climb per distance, average speed and pace, per-week rates, weighted average heart rate and power, kJ, relative effort), highlights (longest, biggest climb, fastest), a distance chart, and breakdowns by sport, gear, year, month and weekday. The table sorts on any column, pages at 25 to 500 rows, shows a total row for every matching activity (not just the page), and downloads exactly the filtered, sorted rows as CSV. For example, "how many miles since 1 September 2024 on my Trek Fuel" is: date range Custom from 1 Sep 2024, gear Trek Fuel, read Distance.

Refinements are applied in memory to the rows already loaded, so changing them is instant and never queries the database. Average figures are weighted by moving time. Archive-only activities (when the Archive box is ticked) have no gear, so they drop out when a gear is chosen.

## Route maps

**Route maps** draws every route in the filter on one map, grouped into areas by start point (within 60 km), so a trip abroad doesn't shrink your home map to a dot. Click a route to open it. Each activity page shows its own route, using the most detailed source available: the GPS stream, then the detail polyline, then the summary polyline.

Maps are SVG and work offline. Map tiles are off by default, because loading them tells the tile server roughly where you were. You can turn them on, and set the tile URL, in Settings. The default is OpenStreetMap, with attribution shown.

## Backup and restore

**Backup & restore** downloads a zip containing:
* consistent SQLite snapshots, taken with SQLite's online backup API;
* a manifest;
* optionally, the export archive and the streams.

Secrets never live in the databases, so a backup never contains tokens, the Client Secret or the web password.

Restoring a backup:
* It's checked first: format, `PRAGMA quick_check`, and the schema version (backups from a newer app version are refused). You also get warnings, for example if the backup belongs to a different athlete.
* You must type `RESTORE` to confirm.
* Sync is held off while it runs.
* The current database is saved first as `backups/pre-restore-*.db` next to it. The last 3 are kept.
* By default this device's own settings, such as units and relay URL, are kept.

**Importing the web database into the app.** On the web app, open **Backup → Send to the app** to get a code like `ABCD-EFGH-JKMN`. Then in the app, open **Backup → Import from web app** and enter the web address and the code.
* Codes work once, expire after 10 minutes and are about 59 bits of randomness. Ten wrong guesses lock the endpoint for 15 minutes.
* The download must use HTTPS (localhost is allowed for development). It refuses redirects and anything that isn't a zip.
* You can also download a backup file and restore it on the device.

## Adding a report

1. Add a class deriving from `ReportBase` in `StravaReports.Core/Reports` (metadata plus a query), and register it with `services.AddReport<T>()` in `ServiceCollectionExtensions`.
2. Add a Razor view deriving from `ReportViewBase` in `StravaReports.Shared/Reports`, and register it with `services.AddReportView<TReport, TView>()` in `UiServices.cs`.

Navigation picks it up automatically. Raw activity JSON is kept, so metrics that need fields not yet mapped to columns don't require a re-download.

## Reports

The filter bar shows an **Archive** checkbox once the export archive has data. Ticking it adds activities that exist only in the archive to the reports. Activities that are also synced are never counted twice. Archive rows use the export's UTC start time, are left out when a gear filter is set (the export has no gear IDs), and count virtual activities as trainer activities.

| Section | Report | Needs |
|---|---|---|
| All sports | Dashboard, volume trends, consistency, gear mileage | Summary |
| All sports | All data: every activity in a sortable table with search, distance, time, elevation, day and heart-rate/power filters, totals, highlights and breakdowns that follow the filter, and CSV download of the view | Summary |
| All sports | Heart-rate zones: time in zone by week and sport | HR; streams for exact zones |
| All sports | Training load: fitness (42-day), fatigue (7-day), form, ramp rate | Power + FTP, HR, or Relative Effort |
| Running | Pace & distance, records (PBs, PB timeline, biggest/longest), efficiency (pace vs HR, efficiency factor, cadence) | Detail for best efforts |
| Cycling | Volume & speed, power (power curve, NP, IF, TSS, kJ, estimated FTP trend) | Streams for the power curve |
| Activity page | Laps, best efforts, elevation/pace/HR/power/cadence charts, zones, peak power | Streams |

Turn on **Fetch streams** in Settings for the stream-based parts. Heart-rate zones come from the custom bounds in Settings, otherwise 60/70/80/90% of max HR (from Settings, or the highest recorded). Stream zone times are scaled to each activity's moving time, so they always add up to it. Without a stream, the whole moving time goes in the zone of the average HR, and the report shows how many activities were estimated this way.

Training load uses the first method that has data for each activity: power TSS (needs FTP), heart-rate TRIMP, Strava's Relative Effort, then 30 per hour.

## CSV export

**Export CSV** downloads activities, laps, best efforts, gear, weekly and monthly totals, records, heart-rate zones and training load. Each export uses the current filter. Values are in storage units (metres, seconds, m/s) with invariant formatting. Text starting with `=`, `+`, `-` or `@` is prefixed with `'` so a spreadsheet never runs it as a formula. On the web the file downloads; on MAUI it opens the share sheet.

## Export archive (CSV import)

**Export archive** loads Strava's "Download your data" export into a **separate SQLite database**: `strava-archive.db`, next to `strava.db`. To get the export on strava.com, go to Settings → My Account → Download or Delete Your Account → Request your archive. You can upload the whole `.zip`, or just `activities.csv` from inside it.

* Activities are keyed on Strava's activity ID, so you can load the same export, or a newer one, any number of times. New activities are inserted and changed ones updated, using a SHA-256 hash of the row. Unchanged rows are left alone, and nothing is duplicated.
* Every column of every row is kept as JSON, so nothing in the export is lost. Strava's repeated headers (two `Distance` columns, one in km and one in metres; two `Elapsed Time`; and so on) are handled.
* Each import runs in one transaction, so a bad or interrupted file leaves the archive unchanged. The import history shows every batch with its counts.
* The page compares the archive with the synced database and shows activities in both, in the archive only, and synced only. Sync and **Delete all data** never touch the archive; it has its own delete button.

## Where secrets live

| Secret | MAUI | Web |
|---|---|---|
| Client Secret, access and refresh tokens | `SecureStorage` (Keychain on iOS) | `App_Data/secrets.json`, encrypted with Data Protection |
| Web login password | — | Same file, hashed with Identity's `PasswordHasher` |

Nothing secret is stored in source, config files or logs. Non-secret settings live in the `AppSettings` table.

## Status against the PRD

Done: phases 0–5, which is every functional requirement from FR-01 to FR-20. That also covers the bulk-export CSV archive and archive data in the reports. Phase 5 added webhooks (FR-17), backup and restore and the web-to-app import (FR-16), route maps (FR-18), and background sync tuning.

Known limits:
* Webhooks need a public HTTPS deployment.
* iOS has no true background sync; it relies on launch and resume, with the web app as the scheduled syncer, as the PRD's risk table says.
* The app's "Import from web app" screen was checked through the shared page and unit tests, but has not been driven on a device.

Decisions taken on the PRD's open questions: MAUI talks to Strava directly, with its own database. Charts use ApexCharts. Trainer and commute activities are included by default (toggle in Settings and in the filter bar). Run = Run, TrailRun, VirtualRun. Ride = Ride, GravelRide, MountainBikeRide, EBikeRide, EMountainBikeRide, VirtualRide, Velomobile, Handcycle.
