# Holt alle Export-Dateien (CSV/JSON/OBJ/PNG/SVG) von der Quest und legt sie
# unter tools/scans ab.
#
# Aufruf:  .\pull_scans.ps1            (aus dem tools-Ordner)
#          powershell -File tools\pull_scans.ps1   (aus dem Repo-Root)

$ErrorActionPreference = "Stop"

$adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    Write-Error "adb nicht gefunden: $adb"
}

$remote = "/sdcard/Android/data/com.pwrcore.wifiscanner/files/"
$scansDir = Join-Path $PSScriptRoot "scans"
New-Item -ItemType Directory -Force $scansDir | Out-Null

# Ist ein Geraet verbunden?
$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\tdevice$" }
if (-not $devices) {
    Write-Error "Keine Quest verbunden (adb devices ist leer). USB-Kabel + Debugging-Freigabe pruefen."
}

Write-Host "Ziehe $remote -> $scansDir ..."

# adb pull legt den Ordner 'files' als Unterordner an -> in ein Temp-Verzeichnis
# ziehen und den Inhalt flach nach scans verschieben (vorhandene Dateien werden
# ueberschrieben).
$tmp = Join-Path $env:TEMP ("quest_pull_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $tmp | Out-Null
try {
    & $adb pull $remote $tmp
    if ($LASTEXITCODE -ne 0) {
        Write-Error "adb pull fehlgeschlagen (Exit $LASTEXITCODE)."
    }

    $pulled = Get-ChildItem -Recurse -File $tmp
    foreach ($f in $pulled) {
        Move-Item -Force $f.FullName (Join-Path $scansDir $f.Name)
    }
    Write-Host "$($pulled.Count) Datei(en) nach $scansDir uebernommen:"
    $pulled | ForEach-Object { Write-Host "  $($_.Name)" }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
