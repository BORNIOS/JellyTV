# JellyTV

Plugin para Jellyfin orientado a IPTV con URLs efímeras, cadenas de redirección y tokens de corta vida. El plugin centraliza varias fuentes M3U, expone una lista combinada para Live TV y mantiene la reproducción estable cuando el proveedor rota la URL final del stream.

## Objetivo

JellyTV resuelve tres problemas habituales en proveedores IPTV:

1. Las URLs de canal no son estables y pasan por varios redirects antes del stream real.
2. El token final puede caducar en segundos o minutos.
3. El cliente HLS nativo suele fallar cuando el proveedor invalida un token en mitad de la reproducción.

La estrategia actual del plugin es:

- combinar una o más fuentes M3U configuradas en el plugin;
- reescribir cada canal para que entre por `/JellyTV/stream`;
- resolver redirects y detectar tokens en el primer acceso;
- para HLS, derivar la reproducción a un modo broadcast que descarga segmentos upstream una sola vez por canal y los reparte a múltiples clientes Jellyfin.

## Flujo real de reproducción

```text
Fuentes M3U configuradas
        |
        v
GET /JellyTV/m3u
        |
        +--> M3uAggregatorService
              - descarga todas las fuentes habilitadas
              - conserva metadatos EXTINF
              - reescribe cada URL de canal -> /JellyTV/stream?url=<base64>
        |
        v
Jellyfin Live TV consume la M3U combinada
        |
        v
GET /JellyTV/stream?url=<base64>
        |
        +--> SessionManagerService
        |     - crea o reutiliza sesion por URL upstream
        |     - coordina renovaciones y backoff
        |
        +--> RedirectResolverService
              - intenta auto-redirect con cookies compartidas
              - si falla, recorre la cadena manualmente
              - detecta expiracion y token cuando es posible
        |
        +--> Si el stream no es HLS
        |     - redireccion HTTP al URL final
        |
        +--> Si el stream es HLS
              - crea/reutiliza un BroadcastPump por canal
              - precalienta el buffer con el primer segmento
              - redirige a /JellyTV/broadcast?sid=<sessionId>
        |
        v
GET /JellyTV/broadcast?sid=<sessionId>
        |
        +--> BroadcastService / BroadcastPump
              - hace polling del playlist HLS
              - descarga segmentos .ts upstream
              - renueva token si recibe 401/403
              - distribuye un flujo MPEG-TS continuo a todos los suscriptores
```

## Componentes

### Plugin

`Plugin.cs` inicializa los servicios principales, configura el logger propio y puede registrar automáticamente un tuner tipo M3U en Jellyfin apuntando a `/JellyTV/m3u`.

### M3uAggregatorService

- Descarga todas las fuentes habilitadas de `PluginConfiguration.Sources`.
- Mantiene una caché corta de la M3U combinada de 20 segundos.
- Actualiza métricas por fuente: última sincronización, cantidad de canales y último error.
- Reescribe cada URL de canal a Base64 URL-safe para que Jellyfin nunca vea directamente la URL IPTV original.

### RedirectResolverService

- Usa dos clientes HTTP: uno con redirects automáticos y otro con seguimiento manual.
- Comparte cookies entre peticiones del plugin para mantener compatibilidad con proveedores que requieren estado.
- Guarda la cadena de redirects completa para diagnóstico.
- Cuando detecta una URL tokenizada, intenta usar la penúltima URL de la cadena como `PlaylistPollUrl`, de modo que cada polling regenere un token fresco en lugar de reutilizar uno agotado.

### SessionManagerService

- Gestiona las sesiones activas por URL upstream.
- Aplica backoff de 30 segundos cuando el upstream falla con errores no autenticación para no saturar el proveedor.
- Renueva el stream cuando detecta expiración o margen de seguridad insuficiente.
- Para streams con token rotativo, prioriza candidatos de renovación no tokenizados antes de reintentar URLs viejas.

### BroadcastService

- Mantiene un `BroadcastPump` por canal.
- Descarga los segmentos HLS una sola vez y los reparte a todos los suscriptores conectados.
- Mantiene un pequeño ring buffer para que nuevos clientes empiecen a reproducir sin esperar demasiado.
- Evita bucles agresivos de reintento cuando el proveedor devuelve 401/403 o errores temporales.

### JellyTVController

Expone endpoints HTTP para la lista M3U combinada, apertura de canales, broadcast, diagnóstico y lectura del log del plugin.

## Endpoints

Todos los endpoints del controlador actual están marcados con `AllowAnonymous`. Esto simplifica la integración con Live TV y el acceso a diagnóstico desde Jellyfin, pero implica que la exposición del servidor debe controlarse a nivel de red, reverse proxy o seguridad perimetral.

| Endpoint | Uso | Notas |
|---|---|---|
| `/JellyTV/ping` | Health check simple | Devuelve estado del plugin y cantidad de fuentes configuradas |
| `/JellyTV/m3u` | Lista M3U combinada | Puede forzar rebuild con `refresh=1` |
| `/JellyTV/stream?url=<base64>` | Entrada por canal | Crea o reutiliza sesión; para HLS redirige a broadcast |
| `/JellyTV/broadcast?sid=<sessionId>` | Flujo MPEG-TS continuo | Endpoint principal para HLS en modo compartido |
| `/JellyTV/segment?sid=<sessionId>&url=<base64>` | Proxy interno de segmentos o subplaylists | Se usa cuando el plugin debe volver a reescribir un `.m3u8` o servir segmentos concretos |
| `/JellyTV/sessions` | Diagnóstico de sesiones activas | Texto plano |
| `/JellyTV/log?lines=500` | Tail del log propio del plugin | Texto plano; útil para soporte |

## Configuración disponible

La página de configuración embebida permite gestionar fuentes y parámetros globales.

| Opción | Valor por defecto | Descripción técnica |
|---|---|---|
| `Sources` | lista vacía | Conjunto de playlists M3U/IPTV gestionadas por el plugin |
| `AutoRegisterTuner` | `true` | Registra o actualiza automáticamente un tuner M3U de Jellyfin apuntando a `/JellyTV/m3u` |
| `TokenRefreshMarginSeconds` | `30` | Margen máximo para renovar antes del vencimiento; para streams con token corto el servicio reduce internamente el margen efectivo |
| `RequestTimeoutSeconds` | `15` | Timeout de descarga de fuentes M3U y otras operaciones HTTP según el servicio |
| `MaxRedirects` | `10` | Límite de redirects al resolver la URL final |
| `UserAgent` | `Mozilla/5.0 (compatible; JellyTV/1.0)` | User-Agent enviado al proveedor IPTV |
| `VerboseLogging` | `false` | Activa logs adicionales en reescritura y diagnóstico |

Cada fuente M3U conserva además:

- `Name`
- `Url`
- `IsEnabled`
- `LastFetched`
- `LastChannelCount`
- `LastError`

## Uso recomendado

### 1. Compilar

Compilación verificada en este repositorio:

```powershell
dotnet build .\JellyTV.csproj -c Release
```

Salida esperada:

```text
bin\Release\net9.0\JellyTV.dll
```

### 2. Instalar el plugin

Copiar el ensamblado compilado y sus dependencias al directorio de plugins de Jellyfin. En una instalación típica:

- Windows: `%APPDATA%\Jellyfin\Server\plugins\JellyTV\`
- Linux / contenedor: el directorio de plugins configurado por Jellyfin dentro del volumen persistente

Después, reiniciar Jellyfin.

### 3. Configurar fuentes

En `Dashboard -> Plugins -> JellyTV`:

1. Agregar una o más fuentes M3U.
2. Ajustar timeouts, redirects y User-Agent si el proveedor lo requiere.
3. Opcionalmente dejar activado `AutoRegisterTuner`.

### 4. Registrar el tuner M3U en Jellyfin

Hay dos modos:

- automático: con `AutoRegisterTuner=true`, el plugin intenta registrar el tuner al atender `/JellyTV/m3u`;
- manual: usar la URL mostrada en la página del plugin dentro de `Dashboard -> Live TV -> Add Tuner Device -> M3U Tuner`.

La URL expuesta por el plugin es:

```text
http://<host-jellyfin>/JellyTV/m3u
```

## Detección de expiración y tokens

El resolvedor intenta detectar expiración usando nombres de query frecuentes:

- `expires`
- `exp`
- `expiry`
- `end`
- `validuntil`
- `token_expiry`

También intenta extraer tokens desde parámetros frecuentes como:

- `token`
- `access_token`
- `auth`
- `t`
- `key`
- `sig`
- `tokenid`

Si un proveedor usa otra convención, el punto de extensión actual está en la lógica de `RedirectResolverService`.

## Logging y diagnóstico

- El plugin escribe un log propio diario con formato `JellyTV-yyyy-MM-dd.log`.
- El log rota por fecha y elimina archivos con más de 5 días.
- El directorio del log se resuelve intentando primero el directorio de logs de Jellyfin y luego rutas alternativas del sistema.
- La UI del plugin permite abrir las últimas líneas del log y consultar el estado vía `/JellyTV/ping`.

## Requisitos del proyecto

Estado actual del repositorio:

- `TargetFramework`: `net9.0`
- Paquetes Jellyfin: `Jellyfin.Controller 10.11.0`, `Jellyfin.Model 10.11.0`
- Referencia de framework: `Microsoft.AspNetCore.App`
- Versión del plugin: `2.0.1`

## Estructura real del repositorio

```text
JellyTV/
├── Plugin.cs
├── Pluginlog.cs
├── JellyTV.csproj
├── Configuration/
│   ├── ConfigPage.html
│   └── PluginConfiguration.cs
├── Controllers/
│   └── JellyTVController.cs
├── Models/
│   ├── IptvSources.cs
│   └── StreamSession.cs
├── Services/
│   ├── BroadcastService.cs
│   ├── HlsRewriterService.cs
│   ├── M3UAggregatorService.cs
│   ├── RedirectResolverService.cs
│   └── SessionManagerService.cs
└── recorder.ps1
```

## Limitaciones y notas operativas

- La estabilidad final sigue dependiendo del comportamiento del proveedor IPTV.
- Si el proveedor invalida tokens de forma extremadamente agresiva o por IP/concurrencia, el plugin solo puede mitigar una parte del problema.
- El modo broadcast está pensado para HLS; streams no HLS se resuelven y redirigen directamente.
- La M3U combinada tiene una caché corta intencional para reducir presión sobre el upstream cuando Jellyfin o varios clientes reconsultan la lista.
- Los endpoints de diagnóstico y logs son anónimos en la implementación actual; si el servidor se publica a Internet, conviene filtrar acceso a nivel de reverse proxy o red.
