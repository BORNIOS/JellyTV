<#
.SYNOPSIS
    HLS Live Stream Recorder — token-aware, redirect-chain smart
.DESCRIPTION
    Sigue la cadena de redirects manualmente, identifica el nodo que
    genera token fresco (penúltimo antes del 200) y lo usa como punto
    de rotación en cada ciclo. No hardcodea ninguna URL.
.PARAMETER Source
    URL inicial (puede ser un shortlink, CF Worker, proxy, etc.)
.PARAMETER OutDir
    Carpeta de salida para los .ts descargados
.PARAMETER MaxSegments
    Cuántos segmentos grabar antes de terminar (0 = infinito)
.PARAMETER RotateAt
    Fracción de la ventana de segmentos que se consume antes de rotar (0.0–1.0)
    Default 0.65 → rota cuando queda ~35% de vida, dejando margen para el lag
.PARAMETER TokenPropDelay
    Segundos a esperar tras obtener un token nuevo antes de usarlo (default 15)
    Cubre el lag de propagación entre el nodo que emite y el que sirve
#>
param(
    [Parameter(Mandatory)][string] $Source,
    [string]  $OutDir         = ".\segments",
    [int]     $MaxSegments    = 0,
    [double]  $RotateAt       = 0.65,
    [int]     $TokenPropDelay = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─────────────────────────────────────────────
# UTILIDADES
# ─────────────────────────────────────────────

function Write-Log {
    param([string]$Level, [string]$Msg)
    $colors = @{ INFO="Cyan"; WARN="Yellow"; ERROR="Red"; OK="Green"; DIM="DarkGray" }
    $ts = Get-Date -Format "HH:mm:ss.fff"
    Write-Host "[$ts][$Level] $Msg" -ForegroundColor ($colors[$Level] ?? "White")
}

# Hace UN solo salto HTTP sin seguir redirects
# Devuelve: @{ Code; Location; Body }
function Invoke-OneHop {
    param([string]$Url)

    $tmpHeaders = [System.IO.Path]::GetTempFileName()
    $tmpBody    = [System.IO.Path]::GetTempFileName()

    try {
        $null = curl.exe -sS --max-redirs 0 `
            -D $tmpHeaders `
            -o $tmpBody `
            -H "User-Agent: Mozilla/5.0" `
            -H "Accept: */*" `
            $Url 2>&1

        $rawHeaders = Get-Content $tmpHeaders -Raw -ErrorAction SilentlyContinue
        $body       = Get-Content $tmpBody    -Raw -ErrorAction SilentlyContinue

        $code     = 0
        $location = $null

        if ($rawHeaders -match "HTTP/[\d.]+ (\d+)") {
            $code = [int]$matches[1]
        }
        if ($rawHeaders -match "(?im)^location:\s*(.+)$") {
            $location = $matches[1].Trim()
        }

        return [pscustomobject]@{
            Code     = $code
            Location = $location
            Body     = $body
        }
    }
    finally {
        Remove-Item $tmpHeaders, $tmpBody -ErrorAction SilentlyContinue
    }
}

# Sigue la cadena completa hop a hop
# Retorna array de @{ Url; Code; Location; Body }
function Resolve-RedirectChain {
    param([string]$StartUrl, [int]$MaxHops = 15)

    $chain   = [System.Collections.Generic.List[object]]::new()
    $current = $StartUrl

    for ($i = 0; $i -lt $MaxHops; $i++) {
        Write-Log DIM "  HOP $i → $current"
        $hop = Invoke-OneHop $current
        $chain.Add([pscustomobject]@{
            Url      = $current
            Code     = $hop.Code
            Location = $hop.Location
            Body     = $hop.Body
        })

        if ($hop.Code -eq 200) { break }

        if ($hop.Code -in 301,302,303,307,308 -and $hop.Location) {
            $current = $hop.Location
        } else {
            Write-Log WARN "Hop inesperado: code=$($hop.Code) url=$current"
            break
        }
    }
    return $chain
}

# Detecta el nodo que genera token:
# el último 30x cuya Location contiene token, /hlsr/, o query string largo
# Fallback: penúltimo redirect
function Get-RotationNode {
    param($Chain)

    $redirects = @($Chain | Where-Object { $_.Code -in 301,302,303,307,308 })

    if ($redirects.Count -eq 0) {
        return $Chain[0].Url
    }

    $tokenNode = $redirects | Where-Object {
        $_.Location -match '[?&]token='   -or
        $_.Location -match '/hlsr/'       -or
        ($_.Location -match '\?' -and ($_.Location -split '\?')[1].Length -gt 80)
    } | Select-Object -Last 1

    if ($tokenNode) {
        Write-Log INFO "Nodo de rotación detectado (genera token): $($tokenNode.Url)"
        return $tokenNode.Url
    }

    $fallback = $redirects[-1].Url
    Write-Log WARN "No se detectó nodo token, usando penúltimo redirect: $fallback"
    return $fallback
}

# Parsea un M3U8 crudo.
# Segs ahora es array de @{ Path; Duration } con la duración real del #EXTINF.
function Parse-M3U8 {
    param([string]$Raw)

    if ([string]::IsNullOrWhiteSpace($Raw)) { return $null }

    $lines = $Raw -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }

    $td  = ($lines | Where-Object { $_ -like "#EXT-X-TARGETDURATION:*" } | Select-Object -First 1)
    $seq = ($lines | Where-Object { $_ -like "#EXT-X-MEDIA-SEQUENCE:*"  } | Select-Object -First 1)

    # Emparejar cada #EXTINF con el path que le sigue
    $segs        = [System.Collections.Generic.List[object]]::new()
    $pendingDur  = $null

    foreach ($line in $lines) {
        if ($line -match '^#EXTINF:([\d.]+)') {
            $pendingDur = $matches[1]          # ej: "10.010000"
        } elseif (-not $line.StartsWith("#") -and $line) {
            $segs.Add([pscustomobject]@{
                Path     = $line
                Duration = if ($pendingDur) { $pendingDur } else { "10.000000" }
            })
            $pendingDur = $null
        }
    }

    return [pscustomobject]@{
        Target = if ($td)  { [int](($td  -split ":")[1]) } else { 10 }
        Seq    = if ($seq) { [int](($seq -split ":")[1]) } else { -1 }
        Segs   = $segs.ToArray()
        Raw    = $Raw
    }
}

# Obtiene manifest fresco desde el nodo de rotación.
# FIX #1: espera PropDelay segundos tras recibir el token
#         para cubrir el lag de propagación entre alfa y tuenisix.
function Get-FreshManifest {
    param(
        [string] $RotationUrl,
        [int]    $Retries   = 3,
        [int]    $PropDelay = 0,
        [switch] $Silent
    )

    for ($try = 1; $try -le $Retries; $try++) {
        if (-not $Silent) { Write-Log DIM "  Fetching manifest (intento $try)..." }
        $chain = Resolve-RedirectChain $RotationUrl

        $final = $chain | Where-Object { $_.Code -eq 200 } | Select-Object -Last 1
        if ($final -and $final.Body -match "#EXTM3U") {
            $parsed = Parse-M3U8 $final.Body
            if ($parsed -and $parsed.Segs.Count -gt 0) {

                # ── FIX #1: esperar propagación de token antes de usarlo ──
                if ($PropDelay -gt 0) {
                    Write-Log DIM "  Token recibido — esperando propagación (${PropDelay}s)..."
                    Start-Sleep -Seconds $PropDelay
                }

                return [pscustomobject]@{
                    Manifest = $parsed
                    FinalUrl = $final.Url
                    Chain    = $chain
                }
            }
        }

        if (-not $Silent) { Write-Log WARN "  Manifest vacío/inválido en intento $try" }
        if ($try -lt $Retries) { Start-Sleep -Seconds 8 }
    }
    return $null
}

# Extrae el ID numérico de un segmento para deduplicar
function Get-SegId {
    param([string]$Path)
    if ($Path -match '_(\d+)\.ts') { return [int]$matches[1] }
    return $Path.GetHashCode()
}

# Resuelve URL relativa de segmento usando la base del manifest final
function Resolve-SegUrl {
    param([string]$Seg, [string]$BaseUrl)

    if ($Seg -match '^https?://') { return $Seg }

    $uri  = [Uri]$BaseUrl
    $base = "$($uri.Scheme)://$($uri.Host)$(if($uri.Port -notin 80,443){":$($uri.Port)"})"

    if ($Seg.StartsWith("/")) {
        return "$base$Seg"
    } else {
        $dir = ($uri.AbsolutePath -replace "/[^/]*$", "")
        return "$base$dir/$Seg"
    }
}

# Descarga un segmento .ts
function Save-Segment {
    param([string]$Url, [string]$OutPath)

    $result = curl.exe -sS -L --max-redirs 5 --retry 2 `
        -H "User-Agent: Mozilla/5.0" `
        -o $OutPath `
        --write-out "%{http_code}" `
        $Url 2>&1

    return ($result -match "^2\d\d$")
}

# Encola los segmentos nuevos de un manifest (sin duplicados)
# Segs es ahora array de @{ Path; Duration }
# Devuelve cuántos se encolaron
function Enqueue-NewSegs {
    param($Segs, $Queue, $Downloaded)

    $count = 0
    foreach ($s in $Segs) {
        $id = Get-SegId $s.Path
        if (-not $Downloaded.Contains($id)) {
            $Queue.Enqueue($s)     # encola el objeto completo {Path, Duration}
            $count++
        }
    }
    return $count
}

# Escribe una entrada en el sidecar CSV de duraciones
function Write-SidecarEntry {
    param([string]$CsvPath, [int]$Index, [int]$SegId, [string]$Duration, [string]$Filename)

    $line = "$Index,$SegId,$Duration,$Filename"
    Add-Content -Path $CsvPath -Value $line -Encoding UTF8
}

# Genera (o regenera) el M3U8 local a partir del sidecar CSV.
# Listo para Jellyfin/Plex — direct play, sin transcoding.
function Build-LocalM3U8 {
    param([string]$OutDir, [string]$CsvPath, [string]$M3U8Path)

    if (-not (Test-Path $CsvPath)) {
        Write-Log WARN "Build-LocalM3U8: sidecar no encontrado ($CsvPath)"
        return
    }

    $entries = Get-Content $CsvPath -Encoding UTF8 |
        Where-Object { $_ -match '^\d' } |
        ForEach-Object {
            $parts = $_ -split ','
            [pscustomobject]@{
                Index    = [int]$parts[0]
                SegId    = [int]$parts[1]
                Duration = $parts[2]
                Filename = $parts[3]
            }
        } | Sort-Object Index

    if ($entries.Count -eq 0) { return }

    # Detectar huecos en la secuencia de SegIds
    $gaps = 0
    for ($i = 1; $i -lt $entries.Count; $i++) {
        $diff = $entries[$i].SegId - $entries[$i-1].SegId
        if ($diff -gt 1) {
            $gaps++
            Write-Log WARN "  Hueco detectado entre seg=$($entries[$i-1].SegId) y seg=$($entries[$i].SegId) ($($diff-1) segmentos faltantes)"
        }
    }

    # Calcular TARGETDURATION real (el mayor duration redondeado hacia arriba)
    $maxDur = ($entries | ForEach-Object { [double]$_.Duration } | Measure-Object -Maximum).Maximum
    $targetDuration = [Math]::Ceiling($maxDur)

    $sb = [System.Text.StringBuilder]::new()
    $null = $sb.AppendLine("#EXTM3U")
    $null = $sb.AppendLine("#EXT-X-VERSION:3")
    $null = $sb.AppendLine("#EXT-X-TARGETDURATION:$targetDuration")
    $null = $sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:$($entries[0].SegId)")
    $null = $sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD")

    foreach ($e in $entries) {
        $null = $sb.AppendLine("#EXTINF:$($e.Duration),")
        $null = $sb.AppendLine($e.Filename)
    }

    $null = $sb.AppendLine("#EXT-X-ENDLIST")

    [System.IO.File]::WriteAllText($M3U8Path, $sb.ToString(), [System.Text.Encoding]::UTF8)

    $totalDur = ($entries | ForEach-Object { [double]$_.Duration } | Measure-Object -Sum).Sum
    $totalMin = [Math]::Round($totalDur / 60, 1)
    Write-Log OK "M3U8 generado — $($entries.Count) segs, ${totalMin}min, $gaps hueco(s) → $M3U8Path"
}

# ─────────────────────────────────────────────
# MAIN
# ─────────────────────────────────────────────

# FIX: pre-calcular el porcentaje como entero para evitar error de expansión en string
$OutDir      = [System.IO.Path]::GetFullPath($OutDir)
$rotatePct   = [int]($RotateAt * 100)

Write-Log INFO "=== HLS Smart Recorder ==="
Write-Log INFO "Source         : $Source"
Write-Log INFO "OutDir         : $OutDir"
Write-Log INFO "MaxSegments    : $(if($MaxSegments -eq 0){'∞'}else{$MaxSegments})"
Write-Log INFO "RotateAt       : $RotateAt  (rota al consumir ${rotatePct}% de la ventana)"
Write-Log INFO "TokenPropDelay : ${TokenPropDelay}s"

$null = New-Item -ItemType Directory -Force -Path $OutDir

# Rutas del sidecar CSV y del M3U8 local
$sidecarPath = Join-Path $OutDir "segments.csv"
$m3u8Path    = Join-Path $OutDir "playlist.m3u8"

# Inicializar sidecar CSV si no existe
if (-not (Test-Path $sidecarPath)) {
    Set-Content -Path $sidecarPath -Value "index,segId,duration,filename" -Encoding UTF8
}

# ── PASO 1: Resolver cadena inicial y detectar nodo de rotación ──
Write-Log INFO "Resolviendo cadena de redirects inicial..."
$initChain = Resolve-RedirectChain $Source

Write-Log INFO "Cadena detectada:"
foreach ($hop in $initChain) {
    $arrow = if ($hop.Code -eq 200) { "✓" } else { "→" }
    Write-Log DIM "  [$($hop.Code)] $arrow $($hop.Url)"
}

$rotationUrl = Get-RotationNode $initChain
Write-Log OK "Nodo de rotación: $rotationUrl"

# ── PASO 2: Usar el M3U8 que ya obtuvimos en la resolución inicial ──
# El chain ya hizo el trabajo completo: alfa generó token → tuenisix devolvió 200 + M3U8.
# No hacemos un segundo fetch (que causaría token nuevo no propagado aún).
Write-Log INFO "Parseando manifest del chain inicial..."
$initFinal = $initChain | Where-Object { $_.Code -eq 200 } | Select-Object -Last 1
$initParsed = $null
if ($initFinal -and $initFinal.Body -match "#EXTM3U") {
    $initParsed = Parse-M3U8 $initFinal.Body
}

if ($initParsed -and $initParsed.Segs.Count -gt 0) {
    $current = [pscustomobject]@{
        Manifest = $initParsed
        FinalUrl = $initFinal.Url
        Chain    = $initChain
    }
    Write-Log OK "Manifest OK (del chain inicial) — seq=$($initParsed.Seq) target=$($initParsed.Target)s segs=$($initParsed.Segs.Count)"
} else {
    # Fallback: fetch normal si el chain inicial no trajo body útil
    Write-Log WARN "Chain inicial sin body útil — haciendo fetch con PropDelay..."
    $current = Get-FreshManifest $rotationUrl -PropDelay $TokenPropDelay -Retries 5
    if (-not $current) { throw "No se pudo obtener manifest inicial." }
    Write-Log OK "Manifest OK — seq=$($current.Manifest.Seq) target=$($current.Manifest.Target)s segs=$($current.Manifest.Segs.Count)"
}

$m = $current.Manifest
Write-Log OK "Listo — seq=$($m.Seq) target=$($m.Target)s segs=$($m.Segs.Count)"

# FIX #3: rotateAfter más holgado con RotateAt=0.65
# Ej: 10s * 6 segs * 0.65 = 39s → quedan ~21s de vida al rotar
$windowSecs  = $m.Target * $m.Segs.Count
$rotateAfter = [Math]::Floor($windowSecs * $RotateAt)
Write-Log INFO "Rotación de token cada ~${rotateAfter}s (ventana total ~${windowSecs}s)"

# ── PASO 3: Loop de grabación ──
$downloaded = [System.Collections.Generic.HashSet[int]]::new()
$totalSaved = 0
$lastRotate = [DateTime]::UtcNow
$segQueue   = [System.Collections.Generic.Queue[object]]::new()

$null = Enqueue-NewSegs $m.Segs $segQueue $downloaded
Write-Log INFO "Iniciando grabación — $($segQueue.Count) segs en cola..."

# Crear M3U8 vacío inicial para que el archivo siempre exista desde el arranque
if (-not (Test-Path $m3u8Path)) {
    Set-Content -Path $m3u8Path -Value "#EXTM3U`n#EXT-X-VERSION:3`n#EXT-X-TARGETDURATION:$($m.Target)`n#EXT-X-MEDIA-SEQUENCE:$($m.Seq)`n#EXT-X-PLAYLIST-TYPE:VOD" -Encoding UTF8
    Write-Log DIM "M3U8 placeholder creado → $m3u8Path"
}

while ($true) {

    $elapsed = ([DateTime]::UtcNow - $lastRotate).TotalSeconds

    # ── FIX #2: rotar si toca por tiempo O si la cola se vació ──
    $shouldRotate = ($elapsed -ge $rotateAfter) -or ($segQueue.Count -eq 0 -and $elapsed -gt $m.Target)

    if ($shouldRotate) {
        $reason = if ($segQueue.Count -eq 0) { "cola vacía" } else { "$([Math]::Round($elapsed,1))s transcurridos" }
        Write-Log INFO "Rotando token ($reason)..."

        # FIX #1 activo: PropDelay cubre lag de propagación
        $fresh = Get-FreshManifest $rotationUrl -PropDelay $TokenPropDelay -Retries 5

        if ($fresh) {
            $m           = $fresh.Manifest
            $current     = $fresh
            $lastRotate  = [DateTime]::UtcNow
            $windowSecs  = $m.Target * $m.Segs.Count
            $rotateAfter = [Math]::Floor($windowSecs * $RotateAt)

            $newCount = Enqueue-NewSegs $m.Segs $segQueue $downloaded
            Write-Log OK "Token rotado — seq=$($m.Seq) segs=$($m.Segs.Count) nuevos=$newCount queue=$($segQueue.Count) próxima rotación en ~${rotateAfter}s"

            if ($newCount -eq 0) {
                Write-Log DIM "Manifest sin segmentos nuevos, esperando $($m.Target)s..."
                Start-Sleep -Seconds $m.Target
                continue
            }
        } else {
            Write-Log WARN "Rotación fallida — reintentando en $($m.Target)s..."
            Start-Sleep -Seconds $m.Target
            continue
        }
    }

    # ── Sin segmentos que descargar aún ──
    if ($segQueue.Count -eq 0) {
        Write-Log DIM "Cola vacía, esperando $($m.Target)s..."
        Start-Sleep -Seconds $m.Target
        continue
    }

    # ── Descargar siguiente segmento ──
    $segObj  = $segQueue.Dequeue()          # ahora es {Path, Duration}
    $seg     = $segObj.Path
    $segDur  = $segObj.Duration
    $segId   = Get-SegId $seg

    if ($downloaded.Contains($segId)) {
        Write-Log DIM "  Dup skip: $segId"
        continue
    }

    $segUrl  = Resolve-SegUrl $seg $current.FinalUrl
    $outFile = Join-Path $OutDir ("{0:D6}_{1}.ts" -f $totalSaved, $segId)

    Write-Log INFO "↓ seg=$segId dur=${segDur}s → $outFile"
    $ok = Save-Segment $segUrl $outFile

    if ($ok) {
        $null = $downloaded.Add($segId)

        # Registrar en sidecar con duración real del #EXTINF
        Write-SidecarEntry $sidecarPath $totalSaved $segId $segDur (Split-Path $outFile -Leaf)

        $totalSaved++
        $videoSecs = [Math]::Round(($totalSaved * $m.Target), 0)
        Write-Log OK "  OK ($totalSaved guardados) [~${videoSecs}s de video]"

        # Regenerar M3U8 local cada 6 segmentos (un ciclo de ventana)
        if ($totalSaved % 6 -eq 0) {
            Build-LocalM3U8 $OutDir $sidecarPath $m3u8Path
        }
    } else {
        Write-Log WARN "  Falló descarga de seg=$segId (descartado)"
        Remove-Item $outFile -ErrorAction SilentlyContinue
    }

    # ── Condición de salida ──
    if ($MaxSegments -gt 0 -and $totalSaved -ge $MaxSegments) {
        Write-Log OK "Límite de $MaxSegments segmentos alcanzado. Fin."
        break
    }

    Start-Sleep -Milliseconds 300
}

# Generar M3U8 final con todos los segmentos
Build-LocalM3U8 $OutDir $sidecarPath $m3u8Path

$totalVideoSecs = $totalSaved * $m.Target
Write-Log OK "=== Fin. Total: $totalSaved segs (~${totalVideoSecs}s de video) ==="
Write-Log OK "    Playlist : $m3u8Path"
Write-Log OK "    Sidecar  : $sidecarPath"