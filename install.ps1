# Builds GlowSync and installs it to %LOCALAPPDATA%\Programs\GlowSync (no admin rights needed).
# Settings live separately in %LOCALAPPDATA%\GlowSync and survive reinstalls.
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\GlowSync'
$exe = Join-Path $target 'GlowSync.exe'
$out = Join-Path $env:TEMP 'glowsync-publish'

dotnet publish (Join-Path $PSScriptRoot 'src\GlowSync.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (Get-Process GlowSync -ErrorAction SilentlyContinue) {
    # Ask the running copy to turn the strip off and quit; force only if it does not respond.
    & $exe --exit
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process GlowSync -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Get-Process GlowSync -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $target | Out-Null
# The file handle can outlive the process by a moment.
for ($attempt = 1; ; $attempt++) {
    try {
        Copy-Item (Join-Path $out 'GlowSync.exe') $exe -Force
        break
    }
    catch [System.IO.IOException] {
        if ($attempt -ge 20) { throw }
        Start-Sleep -Milliseconds 500
    }
}

$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'GlowSync.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'Подсветка монитора'
$shortcut.Save()

Start-Process $exe -ArgumentList '--autostart'
Write-Host "GlowSync установлен: $exe"
