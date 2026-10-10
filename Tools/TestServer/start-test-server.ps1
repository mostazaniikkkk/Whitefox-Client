# Starts a local vanilla 1.20.4 server in offline mode for testing WhiteFox (localhost only).
# Needs Java 17+ in PATH (or pass -Java "C:\path\to\java.exe"). The server files live next to this script in .\server
param(
    [string]$Java = "java",
    [string]$Memory = "2G"
)

$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot "server"
New-Item -ItemType Directory -Force $root | Out-Null
$jar = Join-Path $root "server.jar"

if (-not (Test-Path $jar)) {
    Write-Host "Downloading the 1.20.4 server from Mojang..."
    $manifest = Invoke-RestMethod "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
    $version = Invoke-RestMethod (($manifest.versions | Where-Object { $_.id -eq "1.20.4" }).url)
    Invoke-WebRequest $version.downloads.server.url -OutFile $jar
    $hash = (Get-FileHash $jar -Algorithm SHA1).Hash.ToLower()
    if ($hash -ne $version.downloads.server.sha1) { Remove-Item $jar; throw "server.jar hash mismatch" }
}

Set-Content (Join-Path $root "eula.txt") "eula=true" -Encoding ascii
$properties = Join-Path $root "server.properties"
if (-not (Test-Path $properties)) {
    @"
online-mode=false
server-ip=127.0.0.1
server-port=25565
spawn-protection=0
view-distance=8
motd=WhiteFox test server
enforce-secure-profile=false
"@ | Set-Content $properties -Encoding ascii
}

Write-Host "Starting the server on 127.0.0.1:25565 (type 'op <name>' in this console to give yourself operator rights)"
Push-Location $root
try { & $Java "-Xmx$Memory" -jar server.jar nogui } finally { Pop-Location }
