# gAId

An AI travel guide on a map. Tell it where you start and what you feel like doing — in any language — and it suggests up to ten real nearby places, one stop at a time, so you build a route as you go.

## How it works

1. **Pick a start point.** The user searches for a starting place (Google Places Autocomplete).
2. **Say what you want.** "Something romantic for the evening", "кофе и десерт", "Wawel Castle"…
3. **The API turns the wish into searches.** OpenAI converts the message into 1–3 English Google Maps queries.
4. **Real places are found nearby.** The Google Places API (Text Search) looks for matches around the current stop.
5. **The model picks the best fits.** OpenAI chooses up to 10 of the found places and writes a short reason for each. It works only from real Google data: rating, review count, price and opening hours, plus up to 10 photos per place.
6. **Pick one, repeat.** The chosen place becomes the next start. Places already visited are excluded.

### Modes

| Mode | Behaviour |
|------|-----------|
| `spot` (default) | One request → one set of suggestions. |
| `plan` | The message is split into ordered steps (up to 4), e.g. "eat, then barber, then the shop". Each step is offered in turn. The steps still to come are returned in `plan`. |

## Tech stack

- **Backend:** ASP.NET Core (.NET 10) Web API with the [OpenAI .NET SDK](https://github.com/openai/openai-dotnet) and the Google Places API (New).
- **Frontend:** React 19, TypeScript, Vite, Tailwind CSS, Framer Motion and [`@vis.gl/react-google-maps`](https://visgl.github.io/react-google-maps/).
- **Hosting:** a single Azure App Service. The API serves the built React app from `wwwroot`.

## Project structure

```
gAId/
├── Back/
│   ├── GuAId.slnx
│   └── GuAId.Api/
│       ├── Controllers/     # RouteController (/api/route), ConfigController (/api/config)
│       ├── Models/          # Request/response DTOs
│       ├── Services/        # RouteService: OpenAI + Google Places logic
│       └── Program.cs
├── frontend/
│   └── src/
│       ├── App.tsx          # App state, route, calls to the API
│       └── components/      # MapBoard, ChatBoard, SliderBoard, StartSearch
└── pack.ps1                 # Build + zip for Azure App Service
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) 20.19+ or 22.12+ (required by Vite)
- An **OpenAI API key**
- A **Google Cloud API key** with these APIs enabled:
  - Places API (New): text search and place photos, called by the server
  - Maps JavaScript API: the map and start-point autocomplete in the browser

## Configuration

| Setting | Environment variable | Description |
|---------|---------------------|-------------|
| `OpenAI:ApiKey` | `OpenAI__ApiKey` | **Required.** OpenAI API key. |
| `OpenAI:Model` | `OpenAI__Model` | Chat model. Default: `gpt-4o-mini`. |
| `Google:ApiKey` | `Google__ApiKey` | **Required.** Server-side key for the Places API. |
| `Google:MapsApiKey` | `Google__MapsApiKey` | Optional browser key for the map. If empty, `Google:ApiKey` is used. |

For local development, create `Back/GuAId.Api/appsettings.Development.json`. Git ignores this file.

```json
{
  "OpenAI": { "ApiKey": "sk-..." },
  "Google": { "ApiKey": "AIza...", "MapsApiKey": "" }
}
```

> The Maps key is sent to the browser through `/api/config`. In production, use a separate `Google:MapsApiKey` restricted by HTTP referrer.

## Running locally

Start the API (it listens on `http://localhost:5104`):

```bash
cd Back/GuAId.Api
dotnet run
```

In a second terminal, start the frontend:

```bash
cd frontend
npm install
npm run dev
```

Open the URL Vite prints (usually `http://localhost:5173`). Vite proxies `/api` requests to the API on port 5104.

## API

### `POST /api/route`

```json
{
  "prompt": "Take me through the historical locations of Krakow",
  "mode": "spot",
  "start": {
    "name": "Main Market Square",
    "google_place_id": "ChIJ...",
    "lat": 50.0617,
    "lng": 19.9373
  },
  "visited": ["ChIJ..."],
  "step": null
}
```

- `start.lat` and `start.lng` are required.
- `mode` is `spot` or `plan`.
- `visited` lists place IDs that should not be suggested again.
- `step` is optional. In `plan` mode, send one step from a previous `plan` response to get places for that step.

The response looks like this:

```json
{
  "text": "Short reply from the guide",
  "locations": [
    {
      "name": "Wawel Castle",
      "google_place_id": "ChIJ...",
      "lat": 50.054, "lng": 19.935,
      "description": "Why this place fits",
      "rating": 4.7,
      "user_rating_count": 51234,
      "rating_summary": "4.7 out of 5, 51234 reviews",
      "review_summary": "",
      "price_level": "", "price": "",
      "open_now": true,
      "opening_hours": ["Monday: 9:00 AM – 5:00 PM"],
      "photos": [{ "url": "https://...", "author": "...", "author_uri": "https://..." }]
    }
  ],
  "plan": [{ "label": "coffee", "queries": [{ "text": "cafe", "named": false }] }]
}
```

Errors use the standard `ProblemDetails` format:

- `400` when the start point or the mode is invalid.
- `500` when an API key is missing.

### `GET /api/config`

This returns `{ "googleMapsApiKey": "..." }` for the frontend map.

## Deployment (Azure App Service)

`pack.ps1` builds the frontend into `Back/GuAId.Api/wwwroot`, publishes the API in Release mode and zips the result:

```powershell
./pack.ps1
# → deploy/guaid.zip
```

1. Create an App Service with the **.NET 10** runtime stack.
2. Add the application settings `OpenAI__ApiKey`, `Google__ApiKey` and, optionally, `Google__MapsApiKey`.
3. Deploy `deploy/guaid.zip`, for example with zip deploy:
   ```bash
   az webapp deploy --resource-group <rg> --name <app> --src-path deploy/guaid.zip --type zip
   ```
