# JellyTV — Instrucciones para GitHub Copilot

> Estado actual: refactorización a HLS Transparent Proxy **completada**.
> La documentación de arquitectura completa está en `AGENTS.md`.

---

## Reglas de código — leer antes de cualquier cambio

### Lo que este plugin hace (y no hace)

- ✅ Proxy transparente de manifiestos HLS y segmentos `.ts`
- ✅ Resolución de cadenas de redirects con gestión de cookies
- ✅ Renovación automática de tokens de corta vida
- ✅ Health check de canales con ffmpeg + fallback HTTP
- ❌ **No remuxea video** — ninguna transformación de formato
- ❌ **No bufferiza segmentos completos** — siempre `CopyToAsync` directo
- ❌ **No usa ffmpeg para streaming** — solo para probar salud de canales

### Invariantes que nunca debes romper

```csharp
// ✅ Correcto — streaming directo
await stream.CopyToAsync(Response.Body, ct);

// ❌ NUNCA bufferizar segmentos completos
var bytes = await response.Content.ReadAsByteArrayAsync();

// ❌ NUNCA remuxear
Process.Start("ffmpeg -i url -c copy out.ts");

// ✅ Sesiones keyed por UpstreamUrl (URL original, nunca la URL tokenizada final)
_sessions[session.UpstreamUrl] = session;

// ✅ Proxies SOLO para resolver la cadena de redirects
// Los segmentos usan el HttpClient directo ("JellyTV"), sin proxy
```

### BroadcastService es código muerto

`BroadcastService.cs` y `BroadcastPump` compilan pero no están registrados ni instanciados.
No los cites como referencia de patrón. El flujo activo pasa por `HlsRewriterService` + `ProxySegmentAsync`.

---

## Arquitectura en una página

```
GET /JellyTV/stream
  → SessionManagerService.GetOrCreateSessionAsync()
  → [renovación proactiva por ventana de playlist]
  → HlsRewriterService.FetchAndRewritePlaylistAsync()
      → descarga m3u8 del proveedor (con proxy rotation si aplica)
      → reescribe cada URI → /JellyTV/segment?sid=X&url=<b64>
  → Content(m3u8, "application/vnd.apple.mpegurl")

GET /JellyTV/segment?sid=X&url=<b64>
  → Sessions.GetSessionById(sid)
  → ProxySegmentAsync()
      intento 1: GET url con cookies actuales
      si 401 → RenewSessionAsync() + remap URL + intento 2
  → stream.CopyToAsync(Response.Body)
  → session.LastSegmentServedAt = now
```

---

## Health check — lógica de 2 etapas

```csharp
// 1. ffmpeg probe (2s de decodificación real, timeout 18s)
var (result, timedOut) = await TryCheckWithFfmpegAsync(...)
if (result != null) return VIVO;

// 2. Si ffmpeg expiró, pre-renovar ANTES del fallback HTTP
if (timedOut) await RenewSessionAsync(session, ct);

// 3. Fallback HTTP (CheckHlsStreamAsync, máx. 2 intentos)
// Intento 1: descarga playlist + segmentos
//   si 401 → renueva + isPostRenewal=true + continue
// Intento 2 (isPostRenewal=true): playlist OK + segmentos presentes = VIVO
//   (no descarga segmentos — el HttpClient del health check no tiene el contexto
//    proxy/cookie de la sesión original, lo que causaría 401 falsos)
```

---

## Archivos más importantes por frecuencia de cambio

| Archivo | Cuándo modificar |
|---------|-----------------|
| `ChannelHealthCheckService.cs` | Lógica de detección de canales muertos |
| `SessionManagerService.cs` | Gestión de sesiones, renovación de tokens |
| `JellyTVController.cs` | Nuevos endpoints, cambios en flujo de stream/segmento |
| `HlsRewriterService.cs` | Reescritura de m3u8, soporte de nuevos tipos de tags |
| `RedirectResolverService.cs` | Detección de tokens, manejo de nuevos tipos de proveedor |
| `PluginConfiguration.cs` | Nuevas opciones de configuración |

---

## Comandos útiles

```powershell
dotnet build .\JellyTV.csproj -c Release   # Build (esperado: 0 errores)
# Diagnostics en runtime:
# GET /JellyTV/sessions  → sesiones activas
# GET /JellyTV/log?lines=200  → log del plugin
# GET /JellyTV/channels  → estado de salud por canal
```