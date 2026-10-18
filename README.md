<div align="center">

<br>

🌐 &nbsp;**Español**&nbsp; · &nbsp;[**English**](README.en.md)

<br>

# 📺 JellyTV

**Plugin para Jellyfin** que convierte tus canales IPTV en un **proveedor nativo de Live TV**:
Directo, FAST y Tokenizado, con renovación automática de tokens y guía EPG enriquecida,
**sin tuner externo, sin transcodificar y sin bufferizar**.

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/JellyTV?style=flat-square&color=00A4DC&label=último%20commit)](https://github.com/BORNIOS/JellyTV/commits/main)
[![CI Build](https://img.shields.io/github/actions/workflow/status/BORNIOS/JellyTV/build.yaml?style=flat-square&color=00A4DC&label=CI)](https://github.com/BORNIOS/JellyTV/actions/workflows/build.yaml)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/JellyTV/total?style=flat-square&color=00A4DC&label=descargas)](https://github.com/BORNIOS/JellyTV/releases)
[![License](https://img.shields.io/github/license/BORNIOS/JellyTV?style=flat-square&color=555)](LICENSE)

</div>

---

## ✨ Características

- 📡 **Proveedor nativo de Live TV** — tus canales aparecen directamente en *Canales* de todos tus clientes (web, móvil, Android TV, iOS…). **No necesitas un tuner M3U externo** ni configurar listas aparte.
- 🔀 **Tres tipos de canal**: **Directo**, **FAST** y **Tokenizado** — gestionados uno a uno desde la configuración.
- 🔑 **Tokens automáticos** — si tu proveedor rota la URL o usa gateways con token (redirects o HTML con `playbackURL` embebido), JellyTV lo resuelve y **mantiene el token fresco solo** (proactivo y reactivo ante 401/403).
- ⏱️ **Modo «token de sesión»** — para proveedores que emiten un token válido **toda la sesión** (p. ej. un `.m3u8` firmado por horas): JellyTV reutiliza el mismo playlist y **no** hace llamadas de renovación innecesarias.
- ⚡ **Sin transcodificar, sin remuxing, sin buffer** — el plugin entrega el stream del proveedor por relay (reescribe el HLS para mantener la cadena viva); el video no se guarda en disco.
- 📺 **Guía EPG a tu medida** — descarga tus fuentes XMLTV, las **filtra a tus canales** y las empuja a tu Worker (menos CPU y almacenamiento en la nube).
- 🎬 **Enriquecimiento TMDB opcional** — títulos, descripciones y año para los programas de tu guía.
- 🛡️ **Health check + failover** — verifica que cada canal está vivo (también por ffmpeg) y **reporta el estado a tu Worker** para conmutación automática.
- 🎨 **Personalización por canal** — logo, categoría, `tvg-id`, User-Agent y cabeceras HTTP propias, y URLs de respaldo por canal.
- 🧩 **Proxies opcionales** — para proveedores que bloquean tu IP, solo durante la resolución de la cadena.

---

## ⚙️ Requisitos

| | |
|---|---|
| **Servidor** | Jellyfin compatible con `Jellyfin.Controller` **10.11.x** |
| **Canales IPTV** | URLs HLS (`.m3u8`) o MPEG-TS de tus proveedores |
| **TMDB** *(opcional)* | Clave de API gratuita en [themoviedb.org](https://www.themoviedb.org/settings/api) para enriquecer la guía |
| **Cloudflare Worker** *(opcional)* | Un Worker (proyecto hermano **JellyTV-Worker**) para EPG, TMDB y failover |

> ✅ JellyTV funciona **solo** como proveedor de Live TV. La integración con EPG/TMDB/Worker
> es **100 % opcional**.

---

## 🚀 Instalación

### Opción A — Desde el repositorio (recomendada)

1. En Jellyfin ve a **Panel → Avanzado → Repositorios de plugins**.
2. Pulsa **Añadir repositorio** y usa la URL del **manifest**:

   ```
   https://raw.githubusercontent.com/BORNIOS/JellyTV/main/manifest.json
   ```
3. Guarda y ve a **Catálogo**, busca **JellyTV** e **Instala**.
4. Reinicia Jellyfin si lo solicita y añade tus canales en **Panel → Plugins → JellyTV**.

### Opción B — Manual

1. Descarga la última versión desde [**Releases**](https://github.com/BORNIOS/JellyTV/releases).
2. Extrae la carpeta `JellyTV_1.0.0` (contiene `Jellyfin.Plugin.JellyTV.dll`) dentro del
   directorio de plugins de tu instalación.
3. Reinicia Jellyfin.
4. Ve a **Panel → Plugins → JellyTV** y añade tus canales.

> 💡 Ubicaciones comunes del directorio de plugins:
> - **Linux / Docker:** `/config/plugins/`
> - **Windows:** `%LOCALAPPDATA%\jellyfin\plugins\`

---

## 🛠️ Configuración

Desde la página del plugin gestionas **cada canal de forma individual** con un editor dedicado:
identidad, entrega/token, EPG, URLs de respaldo, cabeceras HTTP y estado de salud.

| Sección | Qué ajustas |
|---|---|
| **Canales** | Nombre, URL, tipo (**Directo / FAST / Tokenizado**), logo, `tvg-id`, categoría |
| **Entrega & token** | Activar **token de sesión** (canales Tokenizados) o renovación efímera |
| **HTTP & cabeceras** | User-Agent global y cabeceras `Name: Value` por canal |
| **EPG & TMDB** | Fuentes XMLTV a filtrar, API de TMDB y paralelismo de enriquecimiento |
| **Worker** | URL del Cloudflare Worker y API key (EPG + failover) |
| **Avanzado** | Proxy pool (opcional), logs verbosos y frecuencias de las tareas programadas |

### Entender los tipos de canal

| Tipo | Descripción |
|---|---|
| 🟢 **Directo** | Origen abierto (M3U/TS directo). JellyTV lo sirve tal cual. |
| 🔵 **FAST** | Canal FAST con URL estable, sin cadena de tokens. |
| 🟠 **Tokenizado** | El proveedor rota la URL o exige un token (gateway, redirect o HTML con `playbackURL`). JellyTV lo resuelve, lo renueva y lo mantiene vivo **automáticamente**. |

---

## 📡 ¿Cómo se ve en tu Jellyfin?

Sin listas aparte ni «trucos»: tus canales aparecen en la sección **Canales** de tu Jellyfin como
cualquier canal de TV. Abre uno y JellyTV se encarga del resto:

- Si el canal es **Directo/FAST**, se entrega por relay sin tocar el video.
- Si es **Tokenizado**, el plugin resuelve el origen, sirve el HLS reescrito y **renueva el token
  antes de que expire** — o solo cuando hace falta si es un token de sesión.

---

## 🔄 EPG, TMDB y failover (opcional)

| Pieza | Beneficio |
|---|---|
| **Filtro XMLTV → Worker** | Solo envías a la nube los programas de **tus** canales |
| **Enriquecimiento TMDB** | Guía con títulos reales, sinopsis y año |
| **Health check** | Sabes en todo momento qué canales están vivos |
| **Reporte al Worker** | Base para **failover automático** entre canales/URLs |

---

## 🔌 API interna

| Endpoint | Descripción |
|---|---|
| `GET /JellyTV/m3u` | M3U combinada (diagnóstico) |
| `GET /JellyTV/stream?url=…` | Abre un canal (relay HLS o redirect) |
| `GET /JellyTV/segment?sid=…&url=…` | Proxy de segmentos |
| `GET /JellyTV/channels` | Estado de salud de los canales |
| `POST /JellyTV/health-check` | Health check manual en background |
| `POST /JellyTV/refresh-guide` | Re-sincroniza la guía EPG |

---

## ❓ Preguntas frecuentes (FAQ)

<details>
<summary><b>¿Necesito un tuner M3U externo o configurar una lista aparte?</b></summary>

No. JellyTV registra un **proveedor nativo de Live TV**; tus canales aparecen directamente en
*Canales*. El endpoint `/JellyTV/m3u` es solo diagnóstico/retrocompatibilidad y puede convivir
sin interferir.
</details>

<details>
<summary><b>¿Transcodifica o guarda el video?</b></summary>

No. El plugin **no transcodifica, no hace remuxing ni bufferiza en disco**: entrega el stream del
proveedor por relay. Para que Jellyfin no transcodifique estos canales en Live TV se recomienda
usar *Direct Stream* (ver notas técnicas).
</details>

<details>
<summary><b>Mi proveedor cambia el token cada pocos minutos, ¿se corta el canal?</b></summary>

JellyTV renueva el token de forma **proactiva** (antes de que expire) y **reactiva** (si un
segmento responde 401/403). El corte se minimiza al máximo; si el proveedor invalida por IP o
concurrencia, el plugin mitiga el impacto con reintentos y backoff.
</details>

<details>
<summary><b>¿Qué es «token de sesión»?</b></summary>

Algunos gateways entregan un token válido para **toda la sesión** (un `.m3u8` firmado por horas).
Con **token de sesión** activado, JellyTV reutiliza ese playlist y **no** hace renovaciones
proactivas (evita cientos de peticiones innecesarias). Si el token caduca, renueva de forma
reactiva.
</details>

<details>
<summary><b>¿Es obligatorio el Cloudflare Worker o la clave de TMDB?</b></summary>

No. JellyTV funciona como proveedor de Live TV **sin ellos**. El Worker (proyecto **JellyTV-Worker**)
y TMDB solo añaden guía enriquecida y failover.
</details>

<details>
<summary><b>Mi proveedor bloquea la IP de mi servidor, ¿qué hago?</b></summary>

Configura un **proxy** en la pestaña Avanzado: JellyTV lo usará (con round-robin y cooldown por
proxy caído) en la **resolución de la cadena**. Los segmentos se sirven desde la IP real del
servidor, como exigen muchos proveedores.
</details>

---

## 🧑‍💻 Desarrollo

Proyecto alineado al [template oficial de plugins de Jellyfin](https://github.com/jellyfin/jellyfin-plugin-template)
(analizadores, CI y manifiesto). Ver `docs/TECHNICAL.md` para la documentación técnica completa.

```bash
# Compilar (Release)
dotnet build Jellyfin.Plugin.JellyTV.sln -c Release

# Publicar el DLL
dotnet publish Jellyfin.Plugin.JellyTV/Jellyfin.Plugin.JellyTV.csproj -c Release -o publish
```

---

## 📄 Licencia

Este proyecto se distribuye bajo la **GPL-3.0**. Consulta el archivo [`LICENSE`](LICENSE).

> **Descargo:** JellyTV no está afiliado a Jellyfin ni a The Movie Database (TMDB). Úsalo solo con
> proveedores y contenidos para los que tengas los derechos necesarios.
