$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'HerdrBubble'
$exePath = Join-Path $installDir 'HerdrBubble.exe'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDir 'Herdr Bubble.lnk'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process -Name 'HerdrBubble' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

dotnet publish -c Release --no-self-contained -o $installDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $exePath)) {
    throw "Published executable not found at $exePath"
}

Set-ItemProperty -Path $runKeyPath -Name 'HerdrBubble' -Value "`"$exePath`""

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = 'Herdr Bubble floating terminal launcher'
$shortcut.Save()

# Notify Windows Shell to refresh cached icons in Explorer & Start Menu
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShellRefresh {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
"@
[ShellRefresh]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Process -FilePath "$env:WINDIR\System32\ie4uinit.exe" -ArgumentList '-show' -NoNewWindow -ErrorAction SilentlyContinue

Write-Output "Installed: $exePath"
Write-Output "Startup:   $runKeyPath\HerdrBubble"
Write-Output "Shortcut:  $shortcutPath"
