# JellyTV - Contexto de debugging (17 Jun 2026)

## Problema
Reproducir streams IPTV con token efímero (alfa → tuenisix?token=XXX) vía Jellyfin Live TV.

## Lo que SÍ funciona
- **TS Proxy**: ProxyTsStreamAsync con reconexión automática. 
  - Prueba del 17:53: 3699 frames, 2+ minutos, 20 reconexiones exitosas
  - Bug corregido: EmptyResult() en vez de StatusCode(502)
  - URL de prueba: http://alfa.../14840.ts

## Lo que NO funciona
- **m3u8 vía TS proxy**: El proxy stremea el playlist como video → falla instantánea
- **m3u8 vía redirect directo**: ffmpeg HLS demuxer reusa URL tokenizada → 403
- **Proxies HTTP**: alfa bloquea IPs de datacenter (156.146.x.x)
- **HLS proxy**: rate-limit de alfa (~1 req/35s)

## Estado actual del código
- GetStream redirige TODO a ProxyTsStreamAsync
- ProxyTsStreamAsync: reconexiones infinitas, EmptyResult al cancelar
- Canal del usuario: .m3u8

## Solución
1. Restaurar lógica dual: .ts → TS proxy, .m3u8 → redirect directo
2. Usar URL .ts (NO .m3u8) para canales tokenizados
3. El TS proxy YA está probado y funciona
