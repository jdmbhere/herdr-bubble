param(
    [switch]$KeepSettings
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'HerdrBubble'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Herdr Bubble.lnk'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process -Name 'HerdrBubble' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

if (Test-Path $runKeyPath) {
    Remove-ItemProperty -Path $runKeyPath -Name 'HerdrBubble' -ErrorAction SilentlyContinue
}

if (Test-Path $shortcutPath) {
    Remove-Item -Path $shortcutPath -Force
}

if (Test-Path $installDir) {
    if ($KeepSettings) {
        Get-ChildItem -Path $installDir -Exclude 'settings.json' | Remove-Item -Recurse -Force
    } else {
        Remove-Item -Path $installDir -Recurse -Force
    }
}

Write-Output 'HerdrBubble uninstalled.'
