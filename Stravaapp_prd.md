Strava Reports PRD
16 Oct 2026 · @Mike
1. Overview and goals
Strava Reports is a personal, local-first reporting app that pulls your Strava activity data into your own database and turns it into cycling and running reports. It ships as a .NET MAUI app (iOS first, other platforms later) and as a web app, both built on one shared codebase.
Problem. Strava's own analysis is locked behind its UI and subscription tiers, and the data is only reachable through a rate-limited API. You want your history stored locally, queryable, and reportable in ways you choose, growing a metric at a time.
Goals
• Pull your full Strava history through the API and keep it in sync incrementally.
• Store everything in a local database so reports run offline and never re-hit the API.
• Show cycling and running metrics, with a design that makes adding the next report cheap.
• Deliver the same features on mobile (MAUI) and web from one shared UI and core library.
• Keep all credentials in a Settings page, never in source code or config files.
Success looks like: a full backfill completes unattended, a new activity appears in reports within one sync, and adding a new metric takes a query plus a component, with no schema change.
2. Scope and users
The primary and only initial user is you, one Strava athlete using your own Strava API application. This keeps the app inside the Standard tier (up to 10 athletes) and removes any need for multi-user accounts.
In scope (v1)
• Strava OAuth connection and token refresh, using your own API app credentials.
• Full historical backfill and incremental sync of activities, laps, gear and (later) streams.
• Local database storage on device (MAUI) and on the server (web).
• Cycling and running reports, date filtering, and sport and gear filtering.
• A Settings page for credentials, sync options and data management.
Out of scope (v1)
• Multi-user accounts, sharing, or a public service for other athletes (needs Extended Access approval).
• Any AI or LLM feature that sends Strava data to a model. Strava's API policy prohibits it, so reports are computed with ordinary code.
• Writing data back to Strava (uploads, edits).
• Social features, segments exploration, and route planning.
• Android and macOS targets are a later option; MAUI keeps them cheap, but they are not tested in v1.
3. Strava API constraints
These constraints drive the design. Items marked verify come from secondary sources or memory and must be checked against Strava's developer docs before build.
Constraint
Detail
Design impact
Subscription
Standard Tier developers need a Strava subscription (required since mid-2026)
You need an active subscription to use your own API app
Athlete cap
Standard tier serves up to 10 athletes
Fine for a personal app; no public distribution
Rate limits
Default limits are per 15 minutes and per day, reported in response headers (verify current numbers on your API settings page)
Sync must be resumable, throttled and header-aware
Token lifetime
Access tokens are short-lived (about 6 hours); a refresh token renews them
Refresh before each sync and persist the new refresh token every time
Scopes
read plus activity:read_all for private activities, optionally profile:read_all
Request only what reports need
AI use
API policy prohibits using API data in AI applications, including prompts, embeddings and RAG
No LLM features over this data
Attribution
Strava's brand guidelines require "Powered by Strava" style attribution (verify)
Add a footer or About entry
API base URL
A new base URL, https://api-v3.strava.com, becomes available on January 4, 2027
Keep the base URL in one configurable place
A per-activity detail call and a streams call are needed for rich metrics, so a full backfill of a large history costs thousands of requests and takes days at default limits. The sync engine in section 6 is designed around that.
4. Architecture
One solution, four projects, so the MAUI app and the web app share all logic and all screens. The UI is written once as Razor components; MAUI hosts it in a Blazor Hybrid web view and the web app serves it directly.
• Core library: domain models, EF Core context, Strava API client, OAuth and token service, sync engine, metric calculations and report queries. No platform code.
• Shared UI library (Razor class library): pages, charts, the Settings page and navigation. Platform differences sit behind interfaces such as ISecretStore and IAuthLauncher.
• MAUI app: Blazor Hybrid host, on-device SQLite, Keychain secrets and WebAuthenticator.
• Web app: Blazor host on .NET 10, server-side SQLite, Data Protection for secrets, a hosted background sync service and (later) a webhook endpoint.
• Test project: unit tests for calculations, the sync state machine and the API client using recorded responses.
Each host keeps its own SQLite database, and both reach Strava through the same core client.
5. Data model
The model stores typed columns for everything reports filter or aggregate on, plus the raw API JSON for each activity. Keeping raw JSON means a new metric never forces a re-download. SQLite is the store on both platforms, accessed through EF Core with code-first migrations.
Table
Key fields
Purpose
Athlete
StravaId, name, units preference
The connected athlete
Activity
StravaId, SportType, StartDateUtc, StartDateLocal, DistanceM, MovingTimeS, ElapsedTimeS, ElevationGainM, AvgSpeed, MaxSpeed, AvgHr, MaxHr, AvgWatts, WeightedAvgWatts, Kilojoules, AvgCadence, GearId, IsTrainer, IsCommute, IsManual, RawJson, DetailFetchedAt
One row per activity; summary fields first, detail added later
ActivityLap
ActivityId, LapIndex, distance, time, avg speed, avg HR
Splits and intervals
ActivityStream
ActivityId, StreamType, compressed data blob
Time series (HR, power, cadence, altitude, velocity), fetched lazily
Gear
StravaId, name, type, retired flag, distance
Bikes and shoes
SyncState
last activity timestamp, backfill cursor, last run, last status
Resumable sync
SyncJob
ActivityId, job type, status, attempts, next attempt
Queue of detail and stream fetches
AppSetting
key, value
Non-secret settings; secrets live in secure storage (section 7)
Design rules:
• Store distances in metres, times in seconds and speeds in m/s; convert for display only.
• Store both UTC and local start times, since local time drives weekly and monthly grouping.
• Index Activity on (SportType, StartDateLocal) and on GearId.
• Deleted or privatised activities are detected by a periodic reconciliation pass, not by every sync.
6. Authentication and sync engine
OAuth flow. You register your own Strava API application, enter its Client ID and Client Secret in Settings, then press Connect. The app opens Strava's authorize page, receives the code on the redirect, exchanges it for tokens and stores them securely. The web app uses a standard server-side redirect endpoint. MAUI uses WebAuthenticator; because Strava's callback setting is a domain, the mobile redirect bounces through the web app's callback endpoint and then into the app's custom URL scheme (see open questions).
Sync stages
1. Refresh token if it expires within 10 minutes; persist the new refresh token immediately.
2. Incremental list: call the athlete activities endpoint with after set to the newest stored activity, paging at the maximum page size, and upsert summaries.
3. Backfill: on first run, page backwards through history using before, saving a cursor so it can resume.
4. Detail queue: for each new activity, enqueue a detail fetch (laps, best efforts, gear) and, if enabled, a streams fetch.
5. Reconcile: weekly, compare recent remote IDs to local ones to catch deletions and edits.
Rate-limit handling
• Read the rate-limit usage headers from every response and stop the run when either the 15-minute or daily limit is near.
• On a 429, sleep until the next 15-minute window; on the daily limit, schedule resume for the next day (UTC reset).
• All stages are idempotent and resumable; a crash or closed app loses no more than one request.
• Summary data lands first so reports work immediately; detail and streams fill in over days.
Where sync runs. The web app runs sync as a hosted background service on a schedule. The MAUI app syncs on launch and on manual refresh, within platform background limits. Phase 4 adds Strava webhooks (web app only) so new activities arrive without polling.
One source of truth. Each platform keeps its own database in v1. The web database is the long-lived one; the app can optionally import it later (see open questions).
7. Settings page and credential storage
No credential appears in source code, config files committed to git, or logs. Everything is entered once in Settings.
Setting
Type
Storage
Strava Client ID
Text
App settings table
Strava Client Secret
Secret, masked
MAUI: SecureStorage (Keychain on iOS). Web: ASP.NET Core Data Protection, encrypted at rest
Access and refresh tokens
Secret
Same as Client Secret
Redirect URI / callback domain
Text, read-only hint
App settings table
Requested scopes
Checkboxes
App settings table
Sync: auto-sync on/off, interval
Toggle, number
App settings table
Sync: fetch streams
Toggle
App settings table
Units
Metric / imperial
App settings table
Week start day
Monday / Sunday
App settings table
Heart-rate zones, FTP, max HR
Numbers
App settings table
Settings page behaviour:
• Connect, Disconnect and Test connection buttons, with the athlete name and token expiry shown when connected.
• Secrets are write-only in the UI: shown masked, replaced rather than revealed.
• Disconnect revokes the Strava authorisation and wipes tokens; a separate Delete all data action empties the database after confirmation.
• Sync status panel: last run, queue length, and remaining rate-limit budget.
• Web settings are protected by a single-user login (ASP.NET Core Identity or a passkey) because the site is internet-facing.
8. Reports and metrics
Reports are built in layers so you can add one at a time. Every report takes a shared filter (date range, sport, gear) and reads only from the local database.
Report
Sport
Metrics
Needs
Dashboard
All
Totals this week, month, year; streak; latest activity; sync status
Summary
Volume trends
All
Distance, time and elevation by week, month, year, with year-on-year overlay
Summary
Consistency
All
Calendar heatmap, activities per week, longest streak
Summary
Running pace and distance
Run
Average pace trend, weekly mileage, long-run progression, pace by distance band
Summary
Running records
Run
Best efforts (5K, 10K, half, marathon), personal-best timeline
Detail
Running efficiency
Run
Pace versus heart rate, cadence trend, heart-rate zone time
Detail, streams
Cycling volume and speed
Ride
Distance, average speed, elevation per ride, climbing per week
Summary
Cycling power
Ride
Average and weighted power, kilojoules, estimated FTP trend
Summary, streams
Gear mileage
Both
Distance per bike and shoe, retirement alerts
Gear
Training load
Both
Acute and chronic load, form, ramp rate
Streams or HR
Activity detail
Both
Splits, laps, charts of HR, power, pace and elevation
Detail, streams
Extensibility. Each report is an IReport implementation holding a query (LINQ over EF Core or SQL) and a Razor component for the view. Reports register themselves in a catalogue the navigation reads, so a new report is one class and one component with no schema change when the data already exists. Charting uses a single Blazor-compatible library chosen in phase 0 (see open questions).
Definitions to settle early: which Strava SportType values count as running (Run, TrailRun, VirtualRun) and cycling (Ride, GravelRide, MountainBikeRide, EBikeRide, VirtualRide), and whether trainer and commute activities are included by default.
9. Functional requirements
Priority: P0 needed for v1, P1 soon after, P2 later.
ID
Requirement
Priority
FR-01
Settings page stores Client ID and Client Secret; secrets are masked and held in secure storage
P0
FR-02
Connect to Strava via OAuth and persist tokens; Disconnect revokes and clears them
P0
FR-03
Refresh the access token automatically before any API call
P0
FR-04
Backfill full activity history, resumable after interruption
P0
FR-05
Incremental sync of new and changed activities, on launch and on a schedule (web)
P0
FR-06
Respect rate limits using response headers; pause and resume without data loss
P0
FR-07
Store activities in a local database with raw JSON retained
P0
FR-08
Dashboard and volume-trend reports with date, sport and gear filters
P0
FR-09
Running pace and cycling speed reports
P0
FR-10
Sync status view: last run, queue size, rate-limit budget, errors
P0
FR-11
Fetch laps, best efforts and gear detail for each activity
P1
FR-12
Running records and gear mileage reports
P1
FR-13
Fetch and store streams; activity detail charts
P1
FR-14
Power, training load and heart-rate zone reports
P1
FR-15
Export reports and raw data to CSV
P1
FR-16
Database backup and restore; import of the web database into the MAUI app
P2
FR-17
Strava webhook receiver for near-real-time sync (web only)
P2
FR-18
Maps of routes using decoded polylines
P2
FR-19
Delete all local data and respect Strava deletion of an activity
P0
FR-20
Show Strava attribution in the app footer or About page
P0
10. Non-functional requirements
• Performance: dashboard renders in under 1 second on a database of 10,000 activities on a recent iPhone; report queries use indexes and avoid loading raw JSON.
• Offline: every report works with no network once data is synced.
• Security: secrets only in secure storage or encrypted at rest; HTTPS only; web app requires login; no secrets in logs or crash reports.
• Privacy: data stays in your own database; no third-party analytics; no data sent to any AI service.
• Reliability: sync is idempotent; the app can be killed mid-sync with no corruption (SQLite WAL mode, transactional upserts).
• Maintainability: one shared core library and one shared UI library; EF Core migrations for every schema change; unit tests on metric calculations and the sync state machine.
• Hosting: web app on Azure App Service (UK South) with SQLite on persistent storage, or Azure SQL if you prefer to move off SQLite later.
• Compatibility: .NET 10, MAUI on iOS 17 and later, evergreen browsers for the web app, responsive layout down to phone width.
• Observability: structured logging of sync runs and API errors, viewable in a log page.
11. Phased roadmap and acceptance criteria
Each phase ends with something usable, so you can start using the app from phase 2 and keep improving it.
1. Phase 0: Foundations. Create the solution (core library, Razor UI library, MAUI Blazor Hybrid app, Blazor web app), EF Core and SQLite with migrations, navigation shell, charting library chosen. Done when: both apps launch, show the same empty dashboard and create an empty database.
2. Phase 1: Connect and sync. Settings page, secure storage, OAuth connect and refresh, summary backfill and incremental sync, rate-limit handling, sync status view. Done when: your full activity history is in the database on both platforms, an interrupted backfill resumes, and no secret appears in a log or file.
3. Phase 2: Core reports. Dashboard, volume trends, consistency, running pace and cycling speed reports with filters. Done when: totals match Strava's own totals for a chosen week and year to within rounding, and reports load in under 1 second.
4. Phase 3: Detail and records. Laps, best efforts, gear, running records, gear mileage, CSV export. Done when: personal bests and bike mileage agree with Strava.
5. Phase 4: Streams and advanced metrics. Streams, activity detail charts, power, heart-rate zones, training load. Done when: a ride and a run each show correct charts, and zone times sum to moving time.
6. Phase 5: Polish and automation. Webhooks, backup and restore, database import into the app, route maps, background sync tuning.
Phases 2 onwards can be reordered freely; each new report is independent once phase 1 data exists.
12. Risks and open questions
Risks
Risk
Likelihood
Mitigation
Strava changes API terms or pricing again
Medium
Keep the API client behind an interface; store raw JSON; keep base URL configurable
Rate limits make backfill slow
High
Summary-first sync, resumable queue, streams optional
Client secret held on a phone
Medium
Personal app only; Keychain storage; option to route token exchange through the web app
Mobile OAuth redirect does not fit Strava's single callback domain
Medium
Spike in phase 0 using a web-app callback that forwards to the app's URL scheme
Data display or caching rules in Strava's terms limit what you may show or keep
Low
Read the current API agreement before phase 2; personal single-athlete use is the design basis
iOS suspends background sync
High
Sync on launch and manual refresh; rely on the web app for scheduled sync
Open questions
• Should the MAUI app talk to Strava directly, or read from the web app's API so there is one database? Direct is simpler and offline-first; shared is simpler to keep consistent. This PRD assumes direct, with import later.
• Which charting library for Blazor: for example ApexCharts, Radzen or MudBlazor charts? Choose in phase 0 after a small spike with a power-versus-time chart.
• Is the web app single-user behind a login, or will you ever share it? Sharing would need Extended Access approval and multi-user tables.
• Do trainer, virtual and commute rides count toward cycling totals by default?
• Do you want Windows and macOS builds of the MAUI app in v1, or iOS only?
• Do you need your older Strava history and any manually imported activities (FIT or GPX) in scope?