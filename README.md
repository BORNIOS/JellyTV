# JellyTV

Plugin para Jellyfin que gestiona fuentes IPTV con URLs efímeras, cadenas de redirección y tokens de corta vida. Centraliza múltiples fuentes M3U, expone una lista combinada para Live TV y mantiene la reproducción estable cuando el proveedor rota la URL final del stream.

> **Versión:** 1.0.0 · **Target:** Jellyfin 10.11.0 · **Runtime:** .NET 9

---

## Tabla de contenidos

- [JellyTV](#jellytv)
  - [Tabla de contenidos](#tabla-de-contenidos)
  - [Instalación](#instalación)
  - [Configuración inicial](#configuración-inicial)
    - [Fuentes M3U](#fuentes-m3u)
    - [Pool de proxies (opcional)](#pool-de-proxies-opcional)
    - [Otras opciones](#otras-opciones)
  - [Cómo funciona](#cómo-funciona)
  - [Flujo de reproducción](#flujo-de-reproducción)
  - [Componentes](#componentes)
    - [`Plugin.cs`](#plugincs)
    - [`M3uAggregatorService`](#m3uaggregatorservice)
    - [`RedirectResolverService`](#redirectresolverservice)
    - [`HlsRewriterService`](#hlsrewriterservice)
    - [`SessionManagerService` (`BackgroundService`)](#sessionmanagerservice-backgroundservice)
    - [`ChannelHealthCheckService`](#channelhealthcheckservice)
    - [`ProxyPoolService`](#proxypoolservice)
    - [`JellyTVController`](#jellytvcontroller)
  - [Endpoints HTTP](#endpoints-http)
  - [Tareas programadas](#tareas-programadas)
  - [Diagnóstico y logs](#diagnóstico-y-logs)
  - [Compilar desde fuente](#compilar-desde-fuente)
  - [Estructura del repositorio](#estructura-del-repositorio)
  - [Notas operativas](#notas-operativas)

---

## Instalación

1. Descarga el `.dll` de la [última release](https://github.com/BORNIOS/JellyTV/releases).
2. Copia el archivo a la carpeta de plugins de Jellyfin:
   - **Windows:** `%APPDATA%\Jellyfin\plugins\JellyTV\`
   - **Linux:** `/var/lib/jellyfin/plugins/JellyTV/`
3. Reinicia Jellyfin.
4. Ve a **Dashboard → Plugins → JellyTV** para configurar las fuentes.

> El plugin registra automáticamente un tuner M3U en Jellyfin apuntando a `/JellyTV/m3u` (configurable, `AutoRegisterTuner: true`). No es necesario añadirlo manualmente.

---

## Configuración inicial

### Fuentes M3U

En la página de configuración del plugin añade una o más fuentes:

| Campo | Descripción |
|-------|-------------|
| **Name** | Nombre de display en la UI |
| **URL** | URL de la playlist M3U del proveedor |
| **Enabled** | Activa / desactiva la fuente sin borrarla |

### Pool de proxies (opcional)

Para proveedores que bloquean la IP del servidor Jellyfin puedes configurar proxies HTTPS. El pool usa round-robin con cooldown de 5 minutos por proxy fallido.

| Campo | Descripción |
|-------|-------------|
| **Host** | Hostname o IP del proxy |
| **Port** | Puerto (generalmente 9002 o 3128) |
| **Enabled** | Activa / desactiva sin borrar |

Los proxies se usan **solo para resolver la cadena de redirects** (obtener la URL final con token). Los segmentos HLS se descargan desde la IP real del servidor.

### Otras opciones

| Opción | Default | Descripción |
|--------|---------|-------------|
| `UserAgent` | `Mozilla/5.0 (compatible; JellyTV/1.0)` | User-Agent enviado al proveedor |
| `VerboseLogging` | `false` | Activa logs DEBUG muy detallados |
| `PreferredAudioLanguages` | `es,spa,es-419` | Idiomas de audio priorizados en `EXT-X-MEDIA` |
| `PreferredSubtitleLanguages` | `es,spa` | Idiomas de subtítulos auto-seleccionados |
| `AutoRegisterTuner` | `true` | Registro automático del tuner en Jellyfin |
| `SourceSyncIntervalHours` | `24` | Frecuencia del sync programado de fuentes |
| `ChannelHealthCheckIntervalHours` | `24` | Frecuencia del health check programado |

---

## Cómo funciona

JellyTV resuelve tres problemas habituales en proveedores IPTV:

1. Las URLs de canal no son estables y pasan por varios redirects antes del stream real.
2. El token final puede caducar en segundos (algunos proveedores lo invalidan cada 4-6 segmentos HLS).
3. El cliente HLS nativo de Jellyfin falla cuando el proveedor invalida un token a mitad de la reproducción.

**Estrategia:** el plugin actúa como un proxy transparente. No hace remuxing ni buffering de video. Se limita a resolver URLs, reescribir manifiestos HLS y inyectar tokens frescos en los segmentos en el momento en que se piden.

---

## Flujo de reproducción

```
Fuentes M3U del proveedor
         │
         ▼
GET /JellyTV/m3u
         │
         └─► M3uAggregatorService
               - descarga todas las fuentes habilitadas
               - conserva metadatos EXTINF (logo, grupo, tvg-id)
               - reescribe cada URL de canal → /JellyTV/stream?url=<base64>
         │
         ▼
Jellyfin Live TV consume la M3U combinada
         │
         ▼
GET /JellyTV/stream?url=<base64>
         │
         ├─► SessionManagerService
         │     - crea o reutiliza sesión por URL upstream
         │     - renueva token proactivamente antes de que expire
         │     - backoff de 15s cuando el upstream falla con error no-auth
         │
         ├─► RedirectResolverService
         │     - intenta auto-redirect con cookies compartidas
         │     - si falla, recorre la cadena hop por hop (máx. 20 redirects)
         │     - detecta token en query string y nodo de rotación
         │     - usa proxy pool round-robin (solo en esta fase)
         │
         ├─► Stream no-HLS → HTTP 302 al URL final
         │
         └─► Stream HLS
               - HlsRewriterService descarga el manifiesto
               - reescribe URIs de segmentos, sub-playlists y EXT-X-MEDIA
               - devuelve un .m3u8 con todo apuntando a /JellyTV/segment
         │
         ▼
GET /JellyTV/segment?sid=<sessionId>&url=<base64>
         │
         └─► ProxySegmentAsync
               - descarga el recurso upstream con cookies actuales
               - si 401/403: renueva sesión, remapea URL desde playlist fresca y reintenta
               - CopyToAsync directo a Response.Body (sin buffer completo)
               - actualiza session.LastSegmentServedAt para inhibir renovaciones innecesarias
```

---

## Componentes

### `Plugin.cs`
Punto de entrada. Instancia todos los servicios, configura el logger propio y registra automáticamente el tuner M3U en Jellyfin (`AutoRegisterTuner`).

### `M3uAggregatorService`
- Descarga y combina todas las fuentes habilitadas.
- Caché interna de 20 segundos para evitar presión repetida en el upstream.
- Reescribe cada URL de canal a Base64 URL-safe para que Jellyfin nunca vea la URL IPTV original.
- Registra métricas por fuente: última sincronización, cantidad de canales, último error.

### `RedirectResolverService`
- Dos clientes HTTP: uno con auto-redirect (preferido, comparte cookies) y uno manual para diagnóstico completo.
- Detecta el nodo de rotación de token (último 3xx cuyo `Location` contiene `?token=`, `/hlsr/` o query string largo).
- Guarda `PlaylistPollUrl` apuntando al penúltimo hop para que cada renovación genere un token nuevo.
- Pool de proxies opcional para la cadena de resolución.

### `HlsRewriterService`
- Descarga y reescribe playlists HLS (master + media).
- Reescribe URIs inline en `#EXT-X-KEY`, `#EXT-X-MAP`, `#EXT-X-MEDIA`.
- Prioriza audio en español y mantiene subtítulos opcionales vía `EXT-X-MEDIA`.
- Caché por sesión de 5 segundos para evitar reescrituras dobles en polls rápidos.
- Analiza la ventana de la playlist (`EXTINF` acumulado + `EXT-X-TARGETDURATION`) para afinar el timing de renovación proactiva.

### `SessionManagerService` (`BackgroundService`)
- Gestiona sesiones por URL upstream (una sesión = un canal).
- Renovación thread-safe por sesión (guarda `IsRenewing` para evitar carrera).
- Limpia sesiones inactivas después de 30 minutos.
- Backoff de 15s por fallo no-auth para no saturar el proveedor.

### `ChannelHealthCheckService`
- Verifica si cada canal está vivo usando **ffmpeg primero** (probe de 2 segundos de decodificación real).
- Si ffmpeg falla o expira (18s), hace fallback a descarga HTTP real (playlist + segmentos).
- Cuando ffmpeg expira, **pre-renueva el token** antes del fallback HTTP para evitar el 401 por token ya vencido.
- En el intento 2 post-renovación, aplica un **lightweight check**: si el m3u8 tiene segmentos presentes, el canal se marca vivo sin descargar los segmentos (evita falsos muertos por falta del contexto proxy/cookie de la resolución original).
- Caché de 30 minutos por canal.

### `ProxyPoolService`
- Round-robin estricto sobre los proxies habilitados en configuración.
- Cooldown de 5 minutos por proxy fallido (sin overhead de timeout en hops subsiguientes).
- Clientes HTTP separados por proxy: uno con auto-redirect y uno sin.

### `JellyTVController`
Expone todos los endpoints HTTP del plugin (ver tabla más abajo).

---

## Endpoints HTTP

Todos son `AllowAnonymous`. Controla el acceso a nivel de red o reverse proxy si el servidor está expuesto a Internet.

| Endpoint | Método | Descripción |
|----------|--------|-------------|
| `/JellyTV/m3u` | GET | M3U combinada. Añade `?refresh=1` para forzar rebuild. |
| `/JellyTV/stream?url=<b64>` | GET | Abre un canal. Devuelve `.m3u8` reescrito (HLS) o redirect (TS). |
| `/JellyTV/stream.m3u8?url=<b64>` | GET | Alias del anterior con extensión explícita. |
| `/JellyTV/segment?sid=<id>&url=<b64>` | GET | Proxy de segmento `.ts` o sub-playlist `.m3u8`. |
| `/JellyTV/sessions` | GET | Estado de todas las sesiones activas (texto plano). |
| `/JellyTV/log?lines=500` | GET | Últimas N líneas del log propio del plugin. |
| `/JellyTV/channels?sourceId=<id>` | GET | Lista de canales con estado de salud cacheado. |
| `/JellyTV/health-check?sourceId=<id>` | POST | Dispara un health check en background. |

---

## Tareas programadas

| Tarea | Key | Descripción |
|-------|-----|-------------|
| **JellyTV: Sync IPTV Sources** | `JellyTVSourceSync` | Re-descarga todas las fuentes M3U y refresca la lista de canales. Intervalo configurable (`SourceSyncIntervalHours`, default: 24h). |
| **JellyTV: Channel Health Check** | `JellyTVChannelHealthCheck` | Verifica si cada canal está vivo. Intervalo configurable (`ChannelHealthCheckIntervalHours`, default: 24h). |

Las tareas también se pueden ejecutar manualmente desde **Dashboard → Scheduled Tasks**.

---

## Diagnóstico y logs

- El plugin escribe su propio log diario: `JellyTV-yyyy-MM-dd.log` en el directorio de logs de Jellyfin.
- Los archivos de más de 5 días se eliminan automáticamente.
- Endpoints útiles para soporte:
  - `/JellyTV/log?lines=200` — tail del log
  - `/JellyTV/sessions` — sesiones activas con URL final, tipo de stream y tiempo de expiración
  - `/JellyTV/channels` — estado de salud con detalles de error por canal
- Activa `VerboseLogging: true` en configuración para logs DEBUG completos (includes reescritura de URIs y cada hop de redirect).

---

## Compilar desde fuente

```powershell
# Requisito: .NET 9 SDK
dotnet build .\JellyTV.csproj -c Release
# Salida: bin\Release\net9.0\JellyTV.dll
```

Build limpio esperado: `0 Errores, 0 Advertencias`.

---

## Estructura del repositorio

```
JellyTV/
├── Plugin.cs                      ← Punto de entrada, wiring de servicios
├── Pluginlog.cs                   ← Logger propio (archivos diarios)
├── ServiceRegistrator.cs          ← Registro de tareas en DI de Jellyfin
├── JellyTV.csproj
├── Configuration/
│   ├── ConfigPage.html            ← UI embebida de configuración
│   └── PluginConfiguration.cs     ← Modelo de configuración
├── Controllers/
│   └── JellyTVController.cs       ← Todos los endpoints HTTP
├── Models/
│   ├── IptvSources.cs             ← IptvSource, ProxyEntry
│   ├── ProxyEntry.cs
│   └── StreamSession.cs           ← StreamSession, ResolvedStream, ChannelHealthStatus
├── ScheduledTask/
│   └── SourceSyncTask.cs          ← Tarea de sync + tarea de health check
└── Services/
    ├── BroadcastService.cs        ← Código legado (no activo en producción)
    ├── ChannelHealthCheckService.cs
    ├── HlsRewriterService.cs
    ├── M3UAggregatorService.cs
    ├── ProxyPoolService.cs
    ├── RedirectResolverService.cs
    └── SessionManagerService.cs
```

---

## Notas operativas

- El plugin **no hace remuxing** ni guarda video en disco. Todo es streaming directo.
- Los proxies configurados se usan **solo para resolver la cadena de redirects**. Los segmentos HLS los sirve la IP real del servidor Jellyfin (algunos proveedores requieren esto).
- Si el proveedor invalida tokens por IP o concurrencia, el plugin no puede compensarlo por completo — solo mitiga el impacto con renovación proactiva y retry.
- `BroadcastService.cs` es código legado que compila pero no está activo. Puede eliminarse en una limpieza futura sin consecuencias.
- Los endpoints son anónimos por diseño para compatibilidad con Jellyfin Live TV. Si expones Jellyfin a Internet, filtra `/JellyTV/*` a nivel de reverse proxy o red.
