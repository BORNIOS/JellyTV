# Jellyfin.Plugin.IptvProxy

Plugin para Jellyfin que actúa como proxy inteligente para servicios IPTV con tokens de autenticación de corta duración y múltiples redirecciones HTTP.

## ¿Qué problema resuelve?

Los servicios IPTV de pago frecuentemente entregan URLs que:
1. Redirigen varias veces (HTTP 301/302) antes de llegar al stream real.
2. Incluyen un token de autenticación en la URL final que expira en minutos.

Los reproductores estándar (incluyendo el cliente Jellyfin base) no saben renovar ese token, lo que provoca cortes cada vez que caduca. Este plugin actúa como intermediario transparente: resuelve las redirecciones, reescribe el playlist HLS para que todo pase por el proxy, y renueva los tokens automáticamente en segundo plano.

## Arquitectura

```
Cliente Jellyfin
    │
    ▼
GET /IptvProxy/stream?url=<url-original>
    │
    ▼
RedirectResolverService  ──►  IPTV Provider
(sigue redirects, detecta token expiry)
    │
    ▼
HlsRewriterService
(reescribe m3u8: cada segmento → /IptvProxy/segment?sid=...&url=...)
    │
    ▼
Playlist reescrito → Cliente
    │
    ▼  (cliente pide segmentos)
GET /IptvProxy/segment?sid=...&url=<segmento-encoded>
    │
    ├── Si token caducó (403/401): RenovarSesión → retry
    └── Stream del segmento .ts → Cliente
    
SessionManagerService (BackgroundService)
    └── Cada 10s: revisa sesiones activas, renueva tokens proactivamente
```

## Instalación

### Opción A — Compilar desde fuente

```bash
cd Jellyfin.Plugin.IptvProxy
dotnet build -c Release
```

Copiar `bin/Release/net8.0/Jellyfin.Plugin.IptvProxy.dll` a:
- **Linux/Docker**: `/config/plugins/IptvProxy/`
- **Windows**: `%APPDATA%\Jellyfin\plugins\IptvProxy\`

Reiniciar Jellyfin.

### Opción B — Repositorio de plugins (cuando esté publicado)

Agregar el repositorio en Jellyfin → Dashboard → Plugins → Repositories y buscar "IPTV Proxy".

## Uso

### 1. Configurar el plugin

Ir a **Dashboard → Plugins → IPTV Proxy** y ajustar:

| Parámetro | Por defecto | Descripción |
|---|---|---|
| Max Redirects | 10 | Máximo de saltos HTTP a seguir |
| Token Refresh Margin | 30s | Segundos antes del vencimiento para renovar |
| Request Timeout | 15s | Timeout de peticiones al proveedor |
| User Agent | Mozilla/5.0... | Cabecera User-Agent enviada al IPTV |
| Verbose Logging | false | Loguea cada reescritura de segmento |

### 2. Integrar con tu lista M3U

Transforma las URLs de tu lista IPTV para que pasen por el proxy:

**URL original en M3U:**
```
http://tu-proveedor.com/live/usuario/pass/canal123
```

**URL para Jellyfin (Live TV → Tuner):**
```
http://localhost:8096/IptvProxy/stream?url=http%3A%2F%2Ftu-proveedor.com%2Flive%2Fusuario%2Fpass%2Fcanal123
```

Puedes automatizar esto con un script que genere la lista M3U ya transformada.

### 3. Agregar como Tuner en Jellyfin

1. Ir a **Dashboard → Live TV → Add Tuner Device**.
2. Seleccionar **M3U Tuner**.
3. Pegar la URL de tu lista M3U (con las URLs ya transformadas al proxy).
4. Jellyfin pedirá los canales a través del proxy automáticamente.

## Detección de expiración de token

El plugin detecta automáticamente los siguientes parámetros de query como indicadores de expiración:

- `expires`, `exp`, `expiry`, `end`, `validuntil`, `token_expiry`

Ejemplo de URL con token reconocido:
```
https://cdn.proveedor.com/stream/canal?token=abc123&expires=1712000000
```

Si tu proveedor usa un nombre de parámetro diferente, puedes extender `TryParseTokenExpiry` en `RedirectResolverService.cs`.

## Diagrama de renovación de token

```
t=0      ──► Sesión creada, token válido hasta t=300
t=270    ──► SessionManager detecta que faltan 30s (margen configurado)
             └── RenewSessionAsync() → nueva URL con token fresco
t=280    ──► Cliente pide segmento → proxy sirve con token nuevo
t=300    ──► Token antiguo caducó — pero ya fue renovado en t=270 ✓
```

## Consideraciones de seguridad

- El endpoint `/IptvProxy/stream` requiere autenticación Jellyfin (`DefaultAuthorization`).
- Las URLs de segmentos incluyen el `sessionId` para verificar que la sesión existe.
- Las URLs upstream se codifican en Base64 URL-safe; el servidor no expone las credenciales IPTV directamente en logs por defecto.

## Requisitos

- Jellyfin 10.9.x o superior
- .NET 8.0 Runtime (incluido en las instalaciones estándar de Jellyfin)

## Estructura del proyecto

```
Jellyfin.Plugin.IptvProxy/
├── Plugin.cs                          # Entrada principal del plugin
├── PluginServiceRegistrator.cs        # Registro de servicios DI
├── Configuration/
│   ├── PluginConfiguration.cs         # Modelo de configuración
│   └── configPage.html                # UI de configuración en Jellyfin
├── Controllers/
│   └── IptvProxyController.cs         # Endpoints HTTP /stream y /segment
├── Models/
│   └── StreamSession.cs               # Modelos de sesión y stream resuelto
└── Services/
    ├── RedirectResolverService.cs     # Sigue redirecciones HTTP
    ├── HlsRewriterService.cs          # Reescribe playlists HLS
    └── SessionManagerService.cs       # Gestiona sesiones y renueva tokens
```
