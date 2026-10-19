# MowerManager

My problem: I have a Husqvarna Automower that I occasionally put in a "secondary area" that has no direct route to the mower's docking station/charger. When I do this, I almost always forget about it until it runs out of battery. Ideally, Husqvarna would let you configure the mower to stop before it runs out of battery; as of now it only supports stopping after some time intervals or when totally drained. 

This app is my band-aid. It uses the Husqvarna Automower API to get the current battery level of the mower. You can configure it to stop the device after a configured battery percentage one time, or (my setup) stop always when it's in a secondary area and the battery gets low. It should be fire-and-forget once deployed in the latter scenario.

It attempts to respect the API rate limits and takes advantage of websockets rather than periodic polling.

## Setup

1. Create an application at <https://developer.husqvarnagroup.cloud> and connect it to the **Authentication API**, **Connectivity API** and **Automower Connect API** under "Connected APIs". Without these, tokens only get `iam:read` and the event stream returns 403 (granted scope should include `amc:api`). Copy the app key and secret. 
2. Optional: create a Pushover application and grab its API token and your user key. Without them, notifications are skipped and the rule still stops the mower.
3. `cp .env.example .env` and fill it in, including the initial admin (or OIDC settings).

## Run

Published image (amd64 and arm64):

```bash
docker run -d --name mower-manager -p 8080:8080 --env-file .env -v mower-data:/data ghcr.io/kylebavis/mowermanager:latest
```

Or build it yourself:

```bash
docker build -t mower-manager --target final .
```

```bash
docker run -d --name mower-manager -p 8080:8080 --env-file .env -v mower-data:/data mower-manager
```

Or `docker compose up -d --build`. UI at http://localhost:8080.

Run tests:

```bash
docker build --target test .
```

## Config

| Key | Default | |
|---|---|---|
| `Polling__IntervalSeconds` | 300 | REST poll interval while the event stream is down. Husqvarna limit is 10,000 requests/month; 300s ≈ 8,640. |
| `Polling__ConnectedIntervalSeconds` | 1800 | REST poll interval while the event stream is connected (≈1,440/month). |
| `Husqvarna__StreamEnabled` | true | Listen to the WebSocket event stream (`Husqvarna__WebSocketUrl`, default `wss://ws.openapi.husqvarna.dev/v1`). Doesn't count against the quota. |
| `Polling__HistoryDays` | 30 | Snapshot retention. |
| `BatteryRule__Scope` / `__ThresholdPercent` / `__Action` | AnyArea / 20 / Pause | Seeds the DB on first run only; edit afterwards in the UI. Scope: `Off`, `AnyArea`, `SecondaryArea`. Action: `Pause`, `ParkUntilNextSchedule`, `ParkUntilFurtherNotice`, or empty for notify-only. |
| `Pushover__Priority` / `__Device` | 0 / all | |

## Battery rule

Two ways to use it, picked in the UI:

- **Always (any area)** – manual: turn it on when you want it and off when you don't.
- **Automatic in secondary area** – leave it armed; it only fires while the mower reports `SECONDARY_AREA` mode.

Fires once per discharge cycle when battery ≤ threshold while the mower is out (`MOWING`, `LEAVING`, `GOING_HOME`). The stop action is only sent while `MOWING`/`LEAVING`, so a mower heading home is left alone. Resets when battery climbs back above the threshold. Battery and mode changes arrive in near real time from the event stream (`battery-event-v2`, `mower-event-v2`), so the rule fires at the threshold. If the stream is down it falls back to polling, which can overshoot by a few percent.

## Authentication

Everyone who can sign in has full access.

**Local accounts** (`Auth__Local__Enabled`, default true). The first user comes from `Auth__Local__InitialAdminUsername` / `__InitialAdminPassword` when no users exist; manage the rest in the UI. Login is rate limited to 10 attempts/minute per IP.

**OIDC** (`Auth__Oidc__Enabled=true`):

| Key | Default | |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | | Register redirect URI `https://<host>/signin-oidc` and post-logout URI `https://<host>/signout-callback-oidc`. |
| `AutoRedirect` | false | Send browsers straight to the provider. With `Auth__Local__Enabled=false` there is no login screen at all. |
| `AllowedEmails__0..n` | any | Restrict which provider accounts get in. Otherwise restrict access at the provider. |
| `DisplayName` | Single sign-on | Button label on the login page. |
| `NameClaim` | preferred_username | |
| `Scopes__0..n` | openid, profile, email | |
| `SignOutOfProvider` | true | Also end the provider session on sign-out. Turn off if the provider has no end-session endpoint. |

Local and OIDC can be enabled together; the login page then offers both. `/login` stays reachable even with `AutoRedirect` on, as a fallback to local login.

**Behind a reverse proxy** (e.g. `https://mower.example.com`), set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so redirects use https.

Cookie keys are stored in `/data/keys`, so sessions survive restarts.

## API

All endpoints except `/health`, `/login` and `/api/auth/*` require sign-in; unauthenticated API calls get 401.

- `GET /api/mowers`, `GET /api/mowers/{id}/history?hours=24`
- `POST /api/mowers/{id}/actions/{Pause|ParkUntilNextSchedule|ParkUntilFurtherNotice|ResumeSchedule}`
- `POST /api/poll`
- `GET|PUT /api/settings`, `GET /api/events`, `POST /api/notifications/test`
- `GET /api/me`, `GET|POST /api/users`, `PUT /api/users/{id}/password`, `DELETE /api/users/{id}` (local accounts)
- `GET /health`

## Database

Schema is managed with EF Core migrations, applied on startup.
