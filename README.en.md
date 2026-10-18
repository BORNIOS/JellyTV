<div align="center">

<br>

🌐 &nbsp;[**Español**](README.md)&nbsp; · &nbsp;**English**

<br>

# 📺 JellyTV

**Jellyfin plugin** that turns your IPTV channels into a **native Live TV provider**:
Direct, FAST and Tokenized, with automatic token renewal and enriched EPG — **no external
tuner, no transcoding, no buffering**.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTV?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTV/commits/main)
[![CI Build](https://img.shields.io/github/actions/workflow/status/BORNIOS/JellyTV/build.yaml?style=flat-square&color=00A4DC&label=CI)](https://github.com/BORNIOS/JellyTV/actions/workflows/build.yaml)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/JellyTV/total?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/JellyTV/releases)
[![License](https://img.shields.io/github/license/BORNIOS/JellyTV?style=flat-square&color=555)](LICENSE)

</div>

---

## ✨ Features

- 📡 **Native Live TV provider** — your channels show up straight in *Channels* on every client (web, mobile, Android TV, iOS…). **No external M3U tuner** and no separate lists to configure.
- 🔀 **Three channel types**: **Direct**, **FAST** and **Tokenized** — managed one by one from the config page.
- 🔑 **Automatic tokens** — if your provider rotates URLs or uses token gateways (redirects or HTML with an embedded `playbackURL`), JellyTV resolves it and **keeps the token fresh on its own** (proactive and reactive on 401/403).
- ⏱️ **"Session token" mode** — for providers that issue a token valid for the **whole session** (e.g. a `.m3u8` signed for hours): JellyTV reuses that playlist and **skips unnecessary renewal calls**.
- ⚡ **No transcoding, no remuxing, no buffering** — the plugin relays the provider stream (rewriting HLS to keep the chain alive); video is never written to disk.
- 📺 **EPG tailored to you** — downloads your XMLTV sources, **filters them to your channels** and pushes them to your Worker (less cloud CPU and storage).
- 🎬 **Optional TMDB enrichment** — titles, overviews and release year for your guide's programmes.
- 🛡️ **Health check + failover** — verifies every channel is alive (ffmpeg included) and **reports status to your Worker** for automatic failover.
- 🎨 **Per-channel customization** — logo, category, `tvg-id`, its own User-Agent and HTTP headers, plus backup URLs per channel.
- 🧩 **Optional proxies** — for providers that block your IP, used only while resolving the chain.

---

## ⚙️ Requirements

| | |
|---|---|
| **Server** | Jellyfin compatible with `Jellyfin.Controller` **10.11.x** |
| **IPTV channels** | HLS (`.m3u8`) or MPEG-TS URLs from your providers |
| **TMDB** *(optional)* | Free API key from [themoviedb.org](https://www.themoviedb.org/settings/api) for guide enrichment |
| **Cloudflare Worker** *(optional)* | A Worker (sister project **JellyTV-Worker**) for EPG, TMDB and failover |

> ✅ JellyTV works **standalone** as a Live TV provider. EPG/TMDB/Worker integration is
> **100 % optional**.

---

## 🚀 Installation

### Option A — From the repository (recommended)

1. In Jellyfin go to **Dashboard → Advanced → Plugin repositories**.
2. Click **Add repository** and use the **manifest** URL:

   ```
   https://raw.githubusercontent.com/BORNIOS/JellyTV/main/manifest.json
   ```
3. Save, then go to **Catalog**, find **JellyTV** and **Install**.
4. Restart Jellyfin if prompted and add your channels under **Dashboard → Plugins → JellyTV**.

### Option B — Manual

1. Download the latest version from [**Releases**](https://github.com/BORNIOS/JellyTV/releases).
2. Extract the `JellyTV_1.0.0` folder (contains `Jellyfin.Plugin.JellyTV.dll`) into the plugins
   directory of your install.
3. Restart Jellyfin.
4. Go to **Dashboard → Plugins → JellyTV** and add your channels.

> 💡 Common plugins directories:
> - **Linux / Docker:** `/config/plugins/`
> - **Windows:** `%LOCALAPPDATA%\jellyfin\plugins\`

---

## 🛠️ Configuration

From the plugin page you manage **every channel individually** with a dedicated editor:
identity, delivery/token, EPG, backup URLs, HTTP headers and health status.

| Section | What you adjust |
|---|---|
| **Channels** | Name, URL, type (**Direct / FAST / Tokenized**), logo, `tvg-id`, category |
| **Delivery & token** | Enable **session token** (Tokenized channels) or ephemeral renewal |
| **HTTP & headers** | Global User-Agent and per-channel `Name: Value` headers |
| **EPG & TMDB** | XMLTV sources to filter, TMDB API and enrichment concurrency |
| **Worker** | Cloudflare Worker URL and API key (EPG + failover) |
| **Advanced** | Optional proxy pool, verbose logging and scheduled-task frequencies |

### Understanding channel types

| Type | Description |
|---|---|
| 🟢 **Direct** | Open origin (raw M3U/TS). JellyTV serves it as-is. |
| 🔵 **FAST** | FAST channel with a stable URL, no token chain. |
| 🟠 **Tokenized** | Provider rotates the URL or requires a token (gateway, redirect or HTML `playbackURL`). JellyTV resolves, renews and keeps it alive **automatically**. |

---

## 📡 What it looks like in your Jellyfin

No separate lists or "hacks": your channels appear under **Channels** in your Jellyfin like any
TV channel. Open one and JellyTV handles the rest:

- If the channel is **Direct/FAST**, it is relayed without touching the video.
- If it's **Tokenized**, the plugin resolves the origin, serves the rewritten HLS and **renews
  the token before it expires** — or only when needed for session tokens.

---

## 🔄 EPG, TMDB and failover (optional)

| Piece | Benefit |
|---|---|
| **XMLTV filter → Worker** | Only your **own** channels' programmes are sent to the cloud |
| **TMDB enrichment** | Guide with real titles, overviews and year |
| **Health check** | Know at all times which channels are alive |
| **Worker report** | Basis for **automatic failover** between channels/URLs |

---

## 🔌 Internal API

| Endpoint | Description |
|---|---|
| `GET /JellyTV/m3u` | Combined M3U (diagnostic) |
| `GET /JellyTV/stream?url=…` | Opens a channel (HLS relay or redirect) |
| `GET /JellyTV/segment?sid=…&url=…` | Segment proxy |
| `GET /JellyTV/channels` | Channels health status |
| `POST /JellyTV/health-check` | Manual background health check |
| `POST /JellyTV/refresh-guide` | Re-sync the EPG guide |

---

## ❓ FAQ

<details>
<summary><b>Do I need an external M3U tuner or a separate playlist?</b></summary>

No. JellyTV registers a **native Live TV provider**; your channels appear directly under
*Channels*. The `/JellyTV/m3u` endpoint is diagnostic/backward-compatible only.
</details>

<details>
<summary><b>Does it transcode or store video?</b></summary>

No. The plugin **does not transcode, remux or buffer to disk**: it relays the provider stream.
For Jellyfin not to transcode these Live TV channels, *Direct Stream* is recommended (see
technical notes).
</details>

<details>
<summary><b>My provider rotates the token every few minutes — will the channel cut out?</b></summary>

JellyTV renews the token **proactively** (before expiry) and **reactively** (when a segment
returns 401/403), minimising interruptions. If the provider invalidates by IP/concurrency, the
plugin mitigates it with retries and backoff.
</details>

<details>
<summary><b>What is a "session token"?</b></summary>

Some gateways issue a token valid for the **whole session** (a `.m3u8` signed for hours). With
**session token** enabled, JellyTV reuses that playlist and **skips proactive renewals** (avoiding
hundreds of unnecessary requests). If it expires, it renews reactively.
</details>

<details>
<summary><b>Is the Cloudflare Worker or TMDB key mandatory?</b></summary>

No. JellyTV works as a Live TV provider **without them**. The Worker (project **JellyTV-Worker**)
and TMDB only add enriched guide and failover.
</details>

<details>
<summary><b>My provider blocks my server's IP — what can I do?</b></summary>

Configure a **proxy** under Advanced: JellyTV uses it (round-robin with cooldown per dead proxy)
while **resolving the chain**. Segments are served from your server's real IP, as most providers
require.
</details>

---

## 🧑‍💻 Development

Project aligned with the [official Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)
(analyzers, CI and manifest). See `docs/TECHNICAL.md` for full technical documentation.

```bash
# Build (Release)
dotnet build Jellyfin.Plugin.JellyTV.sln -c Release

# Publish the plugin DLL
dotnet publish Jellyfin.Plugin.JellyTV/Jellyfin.Plugin.JellyTV.csproj -c Release -o publish
```

---

## 📄 License

This project is distributed under the **GPL-3.0**. See the [`LICENSE`](LICENSE) file.

> **Disclaimer:** JellyTV is not affiliated with Jellyfin or The Movie Database (TMDB). Use it
> only with providers and content you are entitled to watch.
