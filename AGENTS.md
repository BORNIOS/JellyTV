# JellyTV — Contexto de Codebase para Agentes IA

> Documento de referencia rápida. Léelo completo antes de modificar cualquier archivo.
> Último estado verificado: mayo 2026 · build 0 errores, 0 advertencias.

---

## Qué es este proyecto

Plugin de Jellyfin (.NET 9, `net9.0`) para gestionar fuentes IPTV con tokens de corta vida.
Actúa como **proxy transparente HLS**: resuelve la cadena de redirects, reescribe el manifiesto
m3u8 para que segmentos y sub-playlists pasen por el plugin, y renueva tokens silenciosamente.

**No hace remuxing. No bufferiza video. No usa ffmpeg para streaming.**
ffmpeg solo se usa para probar si un canal está vivo durante el health check.

---

## Stack

| Capa | Tecnología |
|------|-----------|
| Runtime | .NET 9 / C# 13 |
| Framework web | ASP.NET Core (vía `Microsoft.AspNetCore.App`) |
| Host | Jellyfin 10.11.0 (`Jellyfin.Controller`, `Jellyfin.Model`) |
| Background tasks | `BackgroundService` (`SessionManagerService`) + `IScheduledTask` (Jellyfin) |
| HTTP | `IHttpClientFactory` — clientes nombrados `"JellyTV"` e `"IptvNoRedirect"` |
| Logging propio | `PluginLog` (archivos diarios `JellyTV-yyyy-MM-dd.log`) |

---

## Mapa de archivos

```
Plugin.cs                   ← Wiring de todos los servicios, auto-registro de tuner
PluginLog.cs                ← Logger propio (rotación diaria, max 5 días)
ServiceRegistrator.cs       ← Registra SourceSyncTask y ChannelHealthCheckTask en DI

Configuration/
  PluginConfiguration.cs    ← Modelo de config (Sources, Proxies, flags, intervalos)
  ConfigPage.html           ← UI embebida (EmbeddedResource)

Controllers/
  JellyTVController.cs      ← TODOS los endpoints HTTP del plugin

Models/
  IptvSources.cs            ← IptvSource, ProxyEntry
  ProxyEntry.cs
  StreamSession.cs          ← StreamSession, ResolvedStream, ChannelHealthStatus

ScheduledTask/
  SourceSyncTask.cs         ← Tarea de sync de fuentes M3U (+ ChannelHealthCheckTask inline)

Services/
  BroadcastService.cs       ← CÓDIGO LEGADO — no activo, no usar como referencia
  ChannelHealthCheckService.cs
  HlsRewriterService.cs
  M3UAggregatorService.cs
  ProxyPoolService.cs
  RedirectResolverService.cs
  SessionManagerService.cs
```

---

## Flujo crítico de reproducción

```
GET /JellyTV/stream?url=<b64>
  → SessionManagerService.GetOrCreateSessionAsync()   // reutiliza o crea sesión
  → Sessions.NeedsRenewal() / RefreshTokenizedSessionIfStaleAsync()  // renovación proactiva
  → HlsRewriter.FetchAndRewritePlaylistAsync()        // descarga + reescribe m3u8
  → Content(m3u8 reescrito, "application/vnd.apple.mpegurl")

GET /JellyTV/segment?sid=<id>&url=<b64>
  → Sessions.GetSessionById(sid)
  → ProxySegmentAsync()  // 2 intentos; en 401: renueva + remapea URL + reintenta
  → stream.CopyToAsync(Response.Body)  // sin buffer completo
  → session.LastSegmentServedAt = DateTimeOffset.UtcNow  // actualiza ventana sana
```

---

## Invariantes que NUNCA debes romper

1. **Sin remuxing**: ningún código debe pasar video por ffmpeg para transformar el formato.
2. **Sin buffer completo de segmentos**: siempre `CopyToAsync` directo a `Response.Body`.
3. **`BroadcastService.cs` es código muerto**: no está registrado ni instanciado. No lo cites como ejemplo de patrón activo.
4. **Proxies solo en resolve**: `ProxyPoolService` se usa en `RedirectResolverService` y `HlsRewriterService.FetchPlaylistWithProxyRotationAsync`. Los segmentos se descargan desde la IP real del servidor.
5. **Sesiones keyed por `UpstreamUrl`**: la clave del diccionario es la URL original del canal (no la URL tokenizada final). Cambiar esto rompe la reutilización de sesiones.
6. **`worker-configuration.d.ts` no aplica aquí**: ese es un proyecto Nuxt/Cloudflare distinto. Este repo es solo C#.

---

## Constantes hardcoded importantes

Archivo: `ChannelHealthCheckService.cs`

```csharp
CACHE_TTL_MINUTES = 30           // TTL de salud cacheada por canal
FFMPEG_TIMEOUT_SECONDS = 18      // Tiempo máximo del probe ffmpeg
FFMPEG_PROBE_DURATION_SECONDS = 2  // Cuántos segundos de video decodifica ffmpeg
MIN_BYTES_FOR_SUCCESS = 200_000  // Mínimo para considerar un canal vivo en fallback HTTP
REQUEST_TIMEOUT_SECONDS = 30     // Timeout del fallback HTTP completo
```

Archivo: `RedirectResolverService.cs`
```csharp
MAX_REDIRECTS = 20
REQUEST_TIMEOUT_SECONDS = 30
```

Archivo: `SessionManagerService.cs`
```csharp
SessionTimeout = 30 minutos       // Sesiones inactivas se limpian
ResolveFailureBackoff = 15s       // Backoff por fallo no-auth
```

Archivo: `ProxyPoolService.cs`
```csharp
ProxyCooldown = 5 minutos         // Proxy en cooldown tras fallo
```

---

## Lógica del health check (ChannelHealthCheckService)

Flujo por canal:

```
1. TryCheckWithFfmpegAsync()
   ├── EXIT=0 en < 18s  →  canal VIVO, retorna inmediatamente
   ├── EXIT≠0           →  (null, TimedOut=false) → fallback con token actual
   └── TIMEOUT (18s)    →  (null, TimedOut=true)  → PRE-RENUEVA token antes de fallback

2. [si ffmpegTimedOut] RenewSessionAsync() antes de CheckHlsStreamAsync()

3. CheckHlsStreamAsync()  (máx. 2 intentos)
   Intento 1:
   ├── Descarga playlist → 401/403  →  RenewSessionAsync() + isPostRenewal=true + continue
   └── Descarga segmentos → 401/403 →  RenewSessionAsync() + isPostRenewal=true + continue
   
   Intento 2 (isPostRenewal=true):
   ├── Descarga playlist OK + segmentos presentes  →  canal VIVO (lightweight, sin descargar segmentos)
   └── Playlist falla  →  canal MUERTO
```

**Por qué el lightweight check en intento 2:**
El `HttpClient` del health check es directo (sin proxy/cookies de la sesión original). Los segmentos
requieren el contexto de IP/cookie establecido durante la resolución. Un m3u8 fresco con segmentos
listados es evidencia suficiente de que el canal está transmitiendo.

---

## Patrón de renovación de token

```csharp
// SessionManagerService.RenewSessionAsync()
// 1. Evita carrera con IsRenewing flag
// 2. Intenta cada candidato en orden (BuildRenewalCandidates)
// 3. En éxito: session.LastResolved = nuevaResolución; _resolveFailures.Remove()
// 4. En fallo auth: _resolveFailures[url] = now; return (sin throw)
// 5. En fallo red: throw para que el caller maneje
// 6. finally: session.IsRenewing = false  ← SIEMPRE se resetea
```

Los candidatos de renovación priorizan URLs no tokenizadas (sin `?token=`) para generar
un token nuevo en cada intento, no reutilizar uno ya consumido.

---

## Reescritura HLS (HlsRewriterService)

Cada URI en el m3u8 se convierte en:
```
/JellyTV/segment?sid=<sessionId>&url=<Base64UrlSafe(uriOriginalAbsoluta)>
```

Esto aplica a:
- Líneas de segmento (`.ts`, sin `#`)
- Sub-playlists (`.m3u8`, sin `#`)
- `URI=` dentro de `#EXT-X-KEY`, `#EXT-X-MAP`
- `URI=` dentro de `#EXT-X-MEDIA`

**No** reescribe líneas que empiezan con `#` salvo los casos anteriores.

---

## Endpoints HTTP completos

| Endpoint | Método | Handler |
|----------|--------|---------|
| `/JellyTV/m3u` | GET | `GetCombinedM3u` |
| `/JellyTV/stream` | GET | `GetStream` |
| `/JellyTV/stream.m3u8` | GET | `GetStream` |
| `/JellyTV/segment` | GET | `GetSegment` |
| `/JellyTV/segment.{ext}` | GET | `GetSegment` |
| `/JellyTV/sessions` | GET | `GetSessions` |
| `/JellyTV/log` | GET | `GetPluginLog` |
| `/JellyTV/channels` | GET | `GetChannels` |
| `/JellyTV/health-check` | POST | `StartHealthCheck` |

Todos son `[AllowAnonymous]`.

---

## Modelo de datos clave

### `StreamSession`
```csharp
string SessionId          // GUID único
string UpstreamUrl        // URL original del canal (clave del diccionario)
ResolvedStream? LastResolved  // Resultado de la última resolución
DateTimeOffset LastAccessedAt
DateTimeOffset? LastSegmentServedAt   // Actualizado por ProxySegmentAsync
DateTimeOffset? LastForcedRenewalAt
bool IsRenewing           // Guard anti-carrera para RenewSessionAsync
double? LastPlaylistWindowSeconds     // Calculado por HlsRewriterService
double? LastPlaylistTargetDurationSeconds
```

### `ResolvedStream`
```csharp
string FinalUrl           // URL final con token
string BaseUrl            // Para resolver URIs relativas en el m3u8
string PlaylistPollUrl    // Penúltimo hop (genera token nuevo en cada poll)
bool IsHls
string? TokenValue        // Token extraído del query string
DateTimeOffset? TokenExpiry
List<string> RedirectChain
string? RotationNodeUrl   // Nodo que rota el token (diagnóstico)
string? InitialPlaylistContent  // Consumido una vez por BroadcastPump (legado)
```

### `ChannelHealthStatus`
```csharp
bool IsAlive              // BytesDownloaded >= MIN_BYTES_FOR_SUCCESS && HttpStatus==200
int HttpStatus
long BytesDownloaded
string StreamType         // "hls" | "mpeg2ts" | "unknown"
DateTimeOffset CheckedAt
int DurationMs
string? ErrorMessage
```

---

## Comandos de desarrollo

```powershell
# Build
dotnet build .\JellyTV.csproj -c Release

# Resultado esperado
# Compilación correcta. 0 Advertencias. 0 Errores.
# Salida: bin\Release\net9.0\JellyTV.dll

# Instalar en Jellyfin (Windows)
# Copiar JellyTV.dll a %APPDATA%\Jellyfin\plugins\JellyTV\
# Reiniciar Jellyfin
```

---

## Problemas conocidos / deuda técnica

| Severidad | Descripción | Archivo |
|-----------|-------------|---------|
| 🟡 Medio | `IsRenewing` no es thread-safe (check-then-set sin lock/Interlocked) | `SessionManagerService.cs` |
| 🟡 Medio | TTL de caché fijo (30 min) para canales muertos — tarda en recuperarse | `ChannelHealthCheckService.cs` |
| 🟡 Medio | Playlist/Variant 401 en `CheckHlsStreamAsync` no setea `isPostRenewal=true` (solo el path de segmento lo hace) | `ChannelHealthCheckService.cs` |
| 🟢 Bajo | `BroadcastService.cs` — código muerto, puede eliminarse | `BroadcastService.cs` |
| 🟢 Bajo | Token expira *durante* el probe de ffmpeg (18s > ~15s de vida del token) — renovar antes de llamar a ffmpeg resolvería esto | `ChannelHealthCheckService.cs` |

---

## Anti-patrones — nunca hacer esto

```csharp
// ❌ Remuxear video
Process.Start("ffmpeg -i stream.m3u8 -c copy output.ts")

// ❌ Bufferizar un segmento completo antes de enviarlo
var bytes = await response.Content.ReadAsByteArrayAsync();
return File(bytes, "video/MP2T");
// ✅ Correcto
await stream.CopyToAsync(Response.Body, ct);

// ❌ Cachear URL tokenizada más de 5 segundos como si fuera estable
session.LastResolved = resolved;  // OK — pero no uses FinalUrl directamente en polls

// ❌ Usar BroadcastService como referencia de patrón activo
// — está desconectado del flujo de producción

// ❌ Acceder a event.context.cloudflare — esto no es un proyecto Cloudflare Workers
// — es un plugin .NET/Jellyfin puro
```
