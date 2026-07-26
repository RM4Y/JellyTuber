# JellyTuber — Jellyfin plugin

Same idea as YouTubeSync (`.strm` + `.nfo`, on-demand playback via yt-dlp),
but indexing is done through the **YouTube Data API v3** instead of scraping.
That makes channel/playlist analysis dramatically faster: ~50 videos per API
call, no throttling.

- **Indexing** → YouTube Data API v3 (fast, paginated JSON)
- **Playback** → yt-dlp, one video at a time, only when you press play

## Legal

This is an independent, community project. It is **not affiliated with,
endorsed by, or sponsored by** YouTube, Google LLC, or Jellyfin.

The plugin does not host, mirror, or permanently store any third-party video.
Indexing only writes metadata, thumbnail images, and small `.strm` pointer
files to your own library folder; the actual stream is resolved on demand, at
playback time, directly from YouTube via `yt-dlp`. Nothing is cached or
redistributed by the project itself.

Use it only with content you have the right to access and to watch this way —
your own uploads, public-domain works, Creative Commons–licensed videos, or
any other content whose rights holder and YouTube's Terms of Service permit
this kind of personal, on-demand playback. You are solely responsible for how
you use this software and for complying with YouTube's Terms of Service,
applicable copyright law, and any other regulation in your jurisdiction.

The software is provided "as is", without warranty of any kind — see
[LICENSE](LICENSE).

**This plugin requires `yt-dlp` to be installed to work. The user is
responsible for complying with third-party platforms' terms of service when
using this tool.**

## Prerequisites

Install these on the same host/container as Jellyfin before using the plugin:

- **[yt-dlp](https://github.com/yt-dlp/yt-dlp)** — resolves the actual video
  stream at playback time. Required.
- **ffmpeg** — YouTube only publishes a single combined video+audio file up
  to 360p; anything higher (up to real 4K) is separate video/audio streams
  that the plugin muxes on the fly (stream copy, no re-encode). No separate
  install or config needed: the plugin uses Jellyfin's own bundled ffmpeg
  (the same one configured under **Dashboard → Playback**), via Jellyfin's
  `IMediaEncoder` service.
- **[Deno](https://deno.com/)** — JS runtime yt-dlp uses to solve YouTube's
  n-challenge, needed for 1080p+ formats to resolve. Required by the default
  `YtDlpExtraArgs` config (`--js-runtimes deno:/usr/bin/deno`); adjust the path
  or remove the flag if you install Deno elsewhere or don't need >720p.

## Quick install (Jellyfin plugin repository)

1. **Dashboard → Plugins → Repositories → Add Repository**.
2. Repository name: `JellyTuber`. Repository URL:
   `https://raw.githubusercontent.com/RM4Y/JellyTuber/main/manifest.json`
3. **Dashboard → Plugins → Catalog**, find **JellyTuber**, install it, then
   restart Jellyfin.

This pulls the latest release automatically — no manual build/deploy needed.
Skip straight to [step 4 (Configure)](#4-configure) once installed. The steps
below (Build/Deploy) are only needed if you want to build from source instead.

## 1. Get a YouTube Data API key (free)

1. Go to <https://console.cloud.google.com/>.
2. Create a project (or reuse one).
3. **APIs & Services → Library → search "YouTube Data API v3" → Enable**.
4. **APIs & Services → Credentials → Create credentials → API key**.
5. Copy the key. (Optional: restrict it to the YouTube Data API v3.)

Free quota is 10,000 units/day. This plugin avoids the expensive `search`
endpoint, so a typical setup uses only a handful of units per sync.

## 2. Build

Requires the .NET 9 SDK.

```bash
dotnet publish Jellyfin.Plugin.JellyTuber.csproj \
  -c Release --no-self-contained -o publish/
```

The output folder contains `Jellyfin.Plugin.JellyTuber.dll`.

> If you are **not** on Jellyfin 10.11.x, edit the two `<PackageReference>`
> versions in the `.csproj` AND `targetAbi` in `meta.json` to match your
> server, then rebuild.

## 3. Deploy

```bash
PLUGIN_DIR="/config/plugins/JellyTuber"   # path inside your Jellyfin container/host
mkdir -p "$PLUGIN_DIR"
cp publish/Jellyfin.Plugin.JellyTuber.dll "$PLUGIN_DIR/"
cp meta.json                               "$PLUGIN_DIR/"
```

Restart Jellyfin. The plugin appears under **Dashboard → Plugins**.

Make sure **yt-dlp** is installed and on PATH inside the same
container/host (used only at playback time):

```bash
yt-dlp --version
```

## 4. Configure

**Dashboard → Plugins → JellyTuber**:

- Paste your **API key**.
- Set the **Library folder** to a path inside a Jellyfin library
  (e.g. `/media/youtube`).
- Set the **Jellyfin address** to the URL your devices use to reach the server.
- Add channels/playlists. For each, pick **Episodes in a series**
  (channel = series, year = season) or **Separate movies**.

Then create/point a Jellyfin library at that folder:
- **Series mode** → library type **Shows**
- **Movies mode** → library type **Movies**

> Keep series-mode sources and movies-mode sources in **separate** library
> folders so the content type matches — mixing them in one library is what
> causes the "everything jumbled" problem.

Run **Dashboard → Scheduled Tasks → Sync YouTube (Fast)** once, then let the
6-hour schedule keep it fresh. Later syncs only process new videos, so they're
fast.

## How playback works

Each `.strm` points to `http://<your-server>/JellyTuber/Stream/<videoId>`.
When you press play, the plugin's controller runs yt-dlp to resolve the
stream. YouTube only exposes a combined video+audio URL up to 360p; above
that, video and audio are separate URLs, which the controller muxes on the
fly with **ffmpeg** (stream copy, no re-encode) and pipes straight to the
client — this is what lets quality go all the way up to real 4K instead of
capping at 360p. If a video only exposes a premuxed/HLS URL (the rare
fallback case), the plugin proxies it directly instead, rewriting HLS
playlists so every segment URL also routes back through the plugin
(`/JellyTuber/Proxy`). Either way, bytes always **proxy through your
server** rather than redirecting the client to YouTube's CDN. Resolved URLs
are cached briefly.

This is deliberate, not just convenient: YouTube's CDN (`googlevideo.com`)
URLs are bound to the IP address that resolved them — i.e. your Jellyfin
server. A 302 redirect would hand that URL to whichever device is actually
playing, which fails as soon as the player is on a different network/IP than
the server (the common case for anything but "same LAN as the server"— most
remote clients, mobile data, some VPN setups). Proxying keeps every request to
YouTube's CDN originating from the server, so playback is consistent across
the Jellyfin web player, iOS/Android apps, and any other Jellyfin-compatible
client, regardless of which network the client is on. The tradeoff is that
video traffic now flows through your server's bandwidth instead of the client
fetching it directly.

Both `/JellyTuber/Stream/{id}` and `/JellyTuber/Proxy` are `[AllowAnonymous]`
so the transcoder/clients can fetch them without an auth token. `/Proxy` only
relays to allow-listed YouTube/googlevideo hosts (it can't be used as an open
proxy to arbitrary URLs), but anyone who can reach `/Stream/{id}` can still
have your server resolve and stream a YouTube video on your bandwidth — keep
it on a trusted network, or put it behind your reverse proxy's auth if you
expose Jellyfin publicly.

## Known limits

- No cookie support → age-restricted / members-only videos won't resolve.
- `/c/CustomName` channel URLs aren't directly resolvable by the API; paste the
  `/channel/UC…` or `/@handle` form instead.
- Tested-by-design against Jellyfin 10.11.x; other versions need the version
  bump described in step 2.

## Project layout

```
Plugin.cs                         entry point + config page registration
Configuration/PluginConfiguration.cs   settings model
Configuration/configPage.html     settings UI
YouTube/YouTubeApiClient.cs       Data API v3 client (the fast indexer)
YouTube/Models.cs                 API DTOs + normalised types
ScheduledTasks/YouTubeSyncTask.cs orchestrates the sync (every 6h)
Services/LibraryWriter.cs         writes folders/.strm/.nfo/thumbnails
Services/PlaybackResolver.cs      yt-dlp resolution + URL cache
Services/FfmpegMuxer.cs           launches ffmpeg to mux separate video/audio streams on the fly
Services/HlsPlaylistRewriter.cs   rewrites HLS playlists to route segments through the proxy
Api/PlaybackController.cs         /JellyTuber/Stream/{id} + /JellyTuber/Proxy (resolve + mux/proxy playback)
```

## Self-service page (users add their own channels)

Users can manage their own YouTube channels from a page the plugin serves:

```
https://<your-server>/JellyTuber
```

They sign in with their Jellyfin account, search a YouTuber (up to 20 results),
add/remove channels, and toggle "exclude Shorts" per channel. Each user's
channels sync into a per-user folder: `<LibraryFolder>/<UserName>`.

### Admin setup for per-user libraries

1. For each user, create a Jellyfin library pointing at
   `<LibraryFolder>/<UserName>` (e.g. `/media/youtube/remy`).
2. In **Dashboard → Users → [user] → Access**, restrict library access so each
   user only sees their own.
3. Share the `/JellyTuber` URL with users (bookmarkable).

Notes:
- This is a separate page, not embedded in the Jellyfin library UI — Jellyfin
  plugins cannot inject custom UI into the library browser.
- Channel search uses the YouTube `search.list` endpoint (100 quota units per
  search); results are cached for 30 minutes to conserve quota.
- Access requires a valid Jellyfin login; for a home/family setup the per-user
  scoping is by the signed-in user.

## License

[MIT](LICENSE) — see the Legal section above for usage responsibilities.
