param([switch]$Tat)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\CydAgent.exe'
$startup = [Environment]::GetFolderPath('Startup')
$link = Join-Path $startup 'CYD PC Agent.lnk'

if ($Tat) {
    if (Test-Path $link) { Remove-Item $link }
    Write-Host 'Đã tắt tự chạy CYD PC Agent.'
    return
}

if (-not (Test-Path $exe)) { throw "Chưa có $exe. Chạy build.cmd trước." }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.Description = 'Gửi số liệu máy tính xuống màn hình CYD qua cáp USB'
$shortcut.Save()
Write-Host "Đã bật tự chạy: $link"
Start-Process -FilePath $exe
