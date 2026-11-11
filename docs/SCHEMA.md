# Esquema de datos de JellyTV

JellyTV guarda su estado en uno de dos motores, elegible en **Ajustes → Almacenamiento**:

| Modo | Destino |
|---|---|
| **Auto** | PostgreSQL privado si el plugin *PostgreSQL Database Provider* expone `IPluginSchemaHost`; si no, archivos JSON |
| **PostgreSQL** | Solo el esquema privado `jellytv_<guid>`; si falla, cae a JSON |
| **Json** | Solo archivos locales |

El esquema privado se llama `jellytv_` + el GUID del plugin sin guiones
(`jellytv_f8a3c1d79b2e4a5f8c6d3e1b7f4a2d9c`). Cumple la validación del proveedor
(`^[a-z][a-z0-9_]{0,62}$` con sufijo `_[0-9a-f]{32}`), está registrado a nombre del plugin y
**nunca** toca `public` ni el esquema de Jellyfin.

---

## 1. Modelo relacional (v2)

```
channel 1 ──── n channel_source
   │                   │
   │                   └──── 1 source_status        (estado vivo/muerto, backoff)
   ├──── 1 channel_active_source ──► channel_source (puntero de failover)
   ├──── n health_event                            (histórico append-only)
   └──── n epg_channel ◄─── epg_source_state 1 ─── n epg_programme
```

### `channel`
Un registro por canal configurado. Es la identidad estable (`channel_id`) que usan el M3U, el EPG y
el historial, aunque el usuario cambie el nombre o las URLs.

| Columna | Tipo | Notas |
|---|---|---|
| `channel_id` | `text` PK | Id que viaja en `?ch=` y en `tvg-id` de la guía |
| `name` | `text` | Nombre público |
| `category`, `logo_url`, `tvg_id`, `tvg_name` | `text` | Metadatos de presentación y EPG |
| `is_enabled`, `auto_disabled` | `bool` | `auto_disabled` lo pone el health check |
| `user_agent`, `custom_headers` | `text` | Overrides HTTP por canal |
| `default_type` | `smallint` | Tipo heredado (0 directo, 1 FAST, 2 tokenizado) |
| `sort_order` | `int` | Orden estable del M3U |
| `created_at`, `updated_at` | `timestamptz` | Auditoría |

Índices: `ix_jellytv_channel_tvg` sobre `lower(tvg_id)` (mapeo EPG), `ix_jellytv_channel_enabled`.

### `channel_source` — los respaldos del canal
Cada canal tiene **n fuentes ordenadas**; `priority = 0` es la principal y el resto son respaldos.
La relación es `ON DELETE CASCADE`, así que borrar un canal borra su árbol.

| Columna | Tipo | Notas |
|---|---|---|
| `source_id` | `text` PK | Estable entre ediciones |
| `channel_id` | `text` FK → `channel` | Dueño |
| `priority` | `smallint` | Orden de failover |
| `label`, `url` | `text` | Etiqueta visible y URL |
| `source_type` | `smallint` | Tipo propio de la fuente |
| `session_token` | `bool` | Token válido toda la sesión |
| `is_enabled` | `bool` | Una fuente deshabilitada no entra al failover |

Índices: `ix_jellytv_source_channel (channel_id, priority)` — devuelve los respaldos de un canal ya
ordenados, sin sort en memoria.

### `source_status` — el estado que hace rápido el switch
Separado de `channel_source` a propósito: el health check escribe aquí cada corrida y la
configuración no se reescribe. Una fila por fuente.

| Columna | Notas |
|---|---|
| `source_id` PK | 1:1 con `channel_source` (sin FK para sobrevivir a reordenamientos) |
| `is_alive` | `null` = nunca comprobado, `true` = abre, `false` = caído |
| `last_error`, `last_checked_at` | Última evidencia |
| `consecutive_failures`, `next_check_at` | Backoff: reintentos más espaciados para no martirizar el upstream |

Índice `ix_jellytv_status_alive (is_alive, next_check_at)` — responde “qué toca comprobar ahora”.

### `channel_active_source` — puntero de failover
Una fila por canal con la fuente **en uso**. Es lo que hace que el cambio sea instantáneo y
sobreviva a un reinicio.

| Columna | Notas |
|---|---|
| `channel_id` PK, FK → `channel` | |
| `source_id` | Fuente elegida (validada contra la configuración al hidratar) |
| `reason` | `health`, `open-failure`, `manual`, `seed` |
| `switched_at` | Cuándo se movió |

### `health_event` — histórico
Append-only, con `bigint GENERATED ALWAYS AS IDENTITY`. Un evento por fuente y corrida.
Índice `(channel_id, checked_at DESC)`. Retención: **30 días** (se purga en cada escritura).

### `epg_source_state`
Estado de sincronización por fuente XMLTV: `synced_at`, `summary`, `error`, `channel_count`,
`programme_count`, `priority`. Alimenta la pestaña Guía EPG sin recalcular nada.

### `epg_channel` — alineación EPG ↔ canal
La tabla que garantiza que la guía no se “desalinee”: cada identidad XMLTV que entrega una fuente
queda ligada al canal del plugin que la usa.

| Columna | Notas |
|---|---|
| `source_id`, `xmltv_id` | PK compuesta; `source_id` FK con `ON DELETE CASCADE` |
| `channel_id` | Canal del plugin (se anula si el canal desaparece) |
| `display_name`, `icon_url` | Para diagnóstico y UI |
| `match_method` | `tvg-id`, `tvg-name`, `none` |

Índice `(channel_id)` — “¿qué XMLTV alimenta este canal?” en una lectura.

### `epg_programme` — programación consultable
Un registro por programa y fuente, con `(source_id, xmltv_id, start_at)` como PK.

| Columna | Notas |
|---|---|
| `start_at`, `stop_at` | `timestamptz`, ya normalizados a UTC |
| `channel_id` | Denormalizado para consultas por canal |
| `title`, `description`, `category`, `icon_url`, `release_year` | Metadatos visibles |

Índices: `(channel_id, start_at)` y `(start_at, stop_at)` (ventana “ahora / próximas horas”).
Retención: se conservan 7 días hacia atrás.

### `tmdb_cache` / `tmdb_negative`
Reservadas para el caché de coincidencias y de “sin resultados” (TTL en `expires_at`).
**Nunca** contienen credenciales: solo el resultado público de TMDB.

### `schema_version`
Una fila por versión aplicada (`1`, `2`). Las migraciones viven en el plugin y se aplican en orden.

---

## 2. Failover casi instantáneo

1. **Hidratación** (arranque): `Load()` reconstruye canales y fuentes ya ordenadas y cruza
   `source_status`; `channel_active_source` llena el mapa `channelId → sourceId` en memoria.
2. **Selección** (`ChannelCatalog.OrderSources`): fuente pedida → puntero activo → vivas por
   prioridad → desconocidas → caídas. Todo con la lista ya ordenada del canal: sin SQL en la ruta
   caliente.
3. **Health check**: recorre el catálogo, actualiza `IsAlive` en memoria y persiste **un solo**
   lote (`health_event` + `source_status`). Si la fuente activa murió, mueve el puntero y lo registra
   como `switched`.
4. **Fallo al abrir** (`/JellyTV/stream`): si una fuente no abre, se marca `IsAlive = false`, se
   mueve el puntero (`open-failure`) y se persiste; la siguiente petición ya arranca por el respaldo.
5. **Reinicio**: el puntero sobrevive, así que no se reintenta primero una fuente que ya estaba caída.

---

## 3. Dónde vive la TMDB API key

**En el XML de configuración de Jellyfin, no en la base de datos.**

`plugins/configurations/Jellyfin.Plugin.JellyTV.xml` es el estándar de Jellyfin
(`BasePlugin<TConfiguration>`), está protegido por los permisos del directorio de datos y es el
único sitio del que el plugin lee secretos. El esquema `jellytv_*` (y la copia JSON) solo guardan
datos operativos: canales, fuentes, salud, EPG y, cuando se cablee, resultados de TMDB.

Motivos para no moverla a PostgreSQL:

- El esquema privado se comparte con el proveedor de base de datos; un volcado o backup lo expone.
- La key debe estar disponible **antes** de inicializar el almacén (la sincronización EPG la usa).
- Jellyfin ya protege y versiona ese XML; duplicarla crearía dos fuentes de verdad.

---

## 4. Equivalencia en el backend JSON

Sin PostgreSQL, el mismo modelo se guarda en archivos dentro de la carpeta de datos
(configurable con una ruta absoluta):

| Tabla | Archivo |
|---|---|
| `channel` + `channel_source` | `channels.json` (fuentes anidadas y ordenadas) |
| `jellytv_proxy` | `proxies.json` |
| `epg_source_state` (config) | `epg-sources.json` |
| `health_event` | `health.json` (últimos 5 000 eventos) |
| `channel_active_source` | `active-sources.json` |
| `epg_source_state` (sync) | `epg-status.json` |
| `epg_channel` | `epg-channels.json` |
| `epg_programme` | `epg-programmes.json` (7 días) |

Todas las escrituras son atómicas (archivo temporal + `File.Move`), así que un corte nunca deja un
JSON a medias.

---

## 5. Migración v1 → v2

La v1 guardaba cada canal como un `jsonb` completo (`jellytv_channel`, `jellytv_epg_source`). Al
arrancar con v2, si `channel` está vacía y las tablas v1 tienen filas, el plugin:

1. Lee los payloads antiguos y los reescribe en el modelo normalizado.
2. Vacía las tablas legacy.
3. Registra `2` en `schema_version`.

Es idempotente: si ya hay datos en v2, no toca nada.
