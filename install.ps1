# Installs a NeoForge dedicated server wired to Lumora Auth via authlib-injector.
# Usage: ./install.ps1 -NeoForgeVersion 21.1.77 -AuthUrl https://auth.example.com
param(
    [Parameter(Mandatory = $true)][string]$NeoForgeVersion,
    [Parameter(Mandatory = $true)][string]$AuthUrl,
    [string]$ServerDir = "$PSScriptRoot/server",
    [string]$Memory = "4G"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $ServerDir | Out-Null
Push-Location $ServerDir
try {
    $installer = "neoforge-$NeoForgeVersion-installer.jar"
    $url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/$NeoForgeVersion/$installer"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $installer
    java -jar $installer --installServer
    if ($LASTEXITCODE -ne 0) { throw "NeoForge installer failed" }
    Remove-Item $installer, "$installer.log" -ErrorAction SilentlyContinue

    # authlib-injector: prefer the copy served by Lumora Auth, fall back to the official mirror.
    $base = $AuthUrl.TrimEnd('/')
    try {
        Invoke-WebRequest "$base/api/launcher/authlib-injector/download" -OutFile authlib-injector.jar
    } catch {
        $meta = Invoke-RestMethod "https://authlib-injector.yushi.moe/artifact/latest.json"
        Invoke-WebRequest $meta.download_url -OutFile authlib-injector.jar
    }

    @(
        "-Xms1G",
        "-Xmx$Memory",
        "-javaagent:authlib-injector.jar=$base/api/yggdrasil"
    ) | Set-Content -Encoding ascii user_jvm_args.txt

    if (-not (Test-Path server.properties)) {
        Copy-Item "$PSScriptRoot/config/server.properties" server.properties
    }
    "eula=true" | Set-Content -Encoding ascii eula.txt
    Write-Host "Done. Start the server with: $ServerDir/run.bat (or run.sh)"
}
finally {
    Pop-Location
}
