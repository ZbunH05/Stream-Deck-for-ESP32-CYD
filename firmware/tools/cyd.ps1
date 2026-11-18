<#
.SYNOPSIS
    Lệnh một chạm cho project CYD Deck (Stream Deck trên ESP32-2432S028R + LVGL 9.5). Chép từ CYD-EEZ.

.DESCRIPTION
    all      (mặc định) Build, nạp, tự kiểm tra loại màn, tự nạp lại đúng env nếu sai,
             rồi mở Serial monitor.
    doctor   Kiểm tra máy: PlatformIO, driver CH340 và cổng COM.
    build    Biên dịch firmware.
    flash    Nạp firmware (tự tìm cổng COM; lỗi ở 921600 baud thì tự thử lại 115200).
    detect   Khởi động lại bo, đọc dòng CYD_PANEL=... để biết màn ILI9341 hay ST7789.
    monitor  Mở Serial monitor (Ctrl+C để thoát).
    clean    Xóa file build.
    shot     Chụp màn hình các cảnh mẫu (src\deck\shot.cpp) bằng cách đọc lại bộ nhớ màn qua Serial, ra PNG
             trong shots\<giờ>; xong tự nạp lại bản thường. Mất vài phút.

.PARAMETER Env
    cyd (1 cổng micro-USB, màn ILI9341) hoặc cyd2usb (2 cổng USB, màn ST7789).
    Bỏ trống: dùng env đã lưu trong tools\board-env.txt (lệnh detect ghi), mặc định cyd.

.PARAMETER NoNet
    Không còn tác dụng (Deck không có mạng, bản chụp luôn dùng dữ liệu mẫu); giữ để lệnh chép từ CYD-EEZ vẫn chạy.

.PARAMETER Compare
    Lệnh shot: thư mục của một lần chụp trước; đếm điểm ảnh khác của từng ảnh, ghi ảnh *-khac.png (chỗ khác tô đỏ).

.PARAMETER ExtraFlags
    Lệnh shot: cờ build thêm cho bản chụp (so A/B một thay đổi, ví dụ -ExtraFlags '-DCYD_NO_IRAM=1').

.EXAMPLE
    .\cyd.cmd
    .\cyd.cmd doctor
    .\cyd.cmd flash -Env cyd2usb -Port COM5
    .\cyd.cmd all -NoMonitor
    .\cyd.cmd shot -Compare shots\20261118-120000
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('all', 'doctor', 'build', 'flash', 'detect', 'monitor', 'clean', 'shot')]
    [string]$Command = 'all',

    [Alias('Env')]
    [ValidateSet('', 'cyd', 'cyd2usb')]
    [string]$Board = '',

    [string]$Port = '',
    [switch]$SkipUi,  # không còn tác dụng (trước 17/11/2026 bỏ bước xuất giao diện từ EEZ Studio), giữ để lệnh cũ vẫn chạy
    [switch]$NoMonitor,
    [switch]$NoNet,
    [string]$Compare = '',
    [string]$ExtraFlags = ''
)

$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $PSScriptRoot
$BoardEnvFile = Join-Path $PSScriptRoot 'board-env.txt'
$DriverUrl = 'https://www.wch-ic.com/downloads/CH341SER_EXE.html'
# Chip USB-UART hay gặp trên bo ESP32: CH340/CH9102 (WCH), CP210x (Silabs), USB của ESP32-S3
$UsbSerialIds = 'VID_1A86|VID_10C4|VID_303A'

# ---------------------------------------------------------------------------
# In ấn
# ---------------------------------------------------------------------------
function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "  [!] $Text" -ForegroundColor Yellow }
function Stop-WithError([string]$Text) { Write-Host "  [LỖI] $Text" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# Công cụ
# ---------------------------------------------------------------------------
function Get-Pio {
    $cmd = Get-Command pio -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $penv = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe'
    if (Test-Path $penv) { return $penv }
    Stop-WithError 'Chưa có PlatformIO Core: https://docs.platformio.org/en/latest/core/installation/index.html'
}

function Get-BoardEnv {
    if ($Board) { return $Board }
    if (Test-Path $BoardEnvFile) {
        $saved = (Get-Content $BoardEnvFile -Raw).Trim()
        if ($saved -in @('cyd', 'cyd2usb')) { return $saved }
    }
    return 'cyd'
}

function Get-SerialPorts {
    @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match '\(COM\d+\)' -and $_.PNPDeviceID -match $UsbSerialIds })
}

# CydAgent (..\..\pc-agent) giữ cổng COM suốt lúc chạy. Trước khi nạp hay đọc log thì
# bảo nó nhả cổng (lệnh "pause" tới 127.0.0.1:47830), xong việc thì "resume". Agent không chạy thì bỏ qua.
$AgentPort = 47830
$script:AgentPaused = $false

function Send-AgentCommand([string]$Text) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $AgentPort, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(300)) { return $null }
        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 3000
        $bytes = [Text.Encoding]::UTF8.GetBytes("$Text`n")
        $stream.Write($bytes, 0, $bytes.Length)
        return (New-Object IO.StreamReader($stream)).ReadLine()
    } catch {
        return $null
    } finally {
        $client.Close()
    }
}

function Suspend-Agent {
    $reply = Send-AgentCommand 'pause 900'
    if ($reply) {
        $script:AgentPaused = $true
        Write-Note 'CYD PC Agent đang chạy: đã bảo nó nhả cổng COM, xong việc sẽ nối lại.'
        Start-Sleep -Milliseconds 400  # đợi agent đóng hẳn cổng
    }
}

function Resume-Agent {
    if ($script:AgentPaused) {
        $null = Send-AgentCommand 'resume'
        $script:AgentPaused = $false
    }
}

function Find-CydPort {
    if ($Port) { return $Port }
    # @() giữ kiểu mảng: Windows PowerShell 5.1 trả .Count rỗng khi chỉ có 1 đối tượng CIM
    $ports = @(Get-SerialPorts)
    if ($ports.Count -ge 1) {
        $com = [regex]::Match($ports[0].Name, '\((COM\d+)\)').Groups[1].Value
        if ($ports.Count -gt 1) { Write-Note "Có $($ports.Count) cổng USB-serial, dùng $com. Chọn cổng khác bằng -Port COMx." }
        return $com
    }
    $noDriver = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match $UsbSerialIds -and $_.Status -ne 'OK' })
    if ($noDriver.Count -gt 0) {
        Stop-WithError "Thấy chip USB-serial ($($noDriver[0].InstanceId)) nhưng chưa có driver. Cài CH341SER.EXE từ $DriverUrl rồi chạy lại."
    }
    Stop-WithError 'Không thấy bo CYD. Cắm lại cáp USB (phải là cáp có truyền dữ liệu, không phải cáp chỉ sạc).'
}

# ---------------------------------------------------------------------------
# Các bước
# ---------------------------------------------------------------------------
function Invoke-Build([string]$TargetEnv) {
    Write-Step "Build firmware (env $TargetEnv)"
    & $Pio run -d $ProjectDir -e $TargetEnv
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Build lỗi, xem thông báo phía trên.' }
    Write-Ok 'Build xong'
}

function Invoke-Flash([string]$TargetEnv, [string]$ComPort) {
    Write-Step "Nạp firmware qua $ComPort (env $TargetEnv)"
    & $Pio run -d $ProjectDir -e $TargetEnv -t upload --upload-port $ComPort
    if ($LASTEXITCODE -ne 0) {
        Write-Note 'Nạp ở 921600 baud bị lỗi, thử lại ở 115200 baud...'
        $env:CYD_UPLOAD_SPEED = '115200'
        try { & $Pio run -d $ProjectDir -e $TargetEnv -t upload --upload-port $ComPort }
        finally { Remove-Item Env:CYD_UPLOAD_SPEED -ErrorAction SilentlyContinue }
        if ($LASTEXITCODE -ne 0) {
            Stop-WithError 'Nạp vẫn lỗi. Thử: giữ nút BOOT, bấm và thả RST, thả BOOT, rồi chạy lại lệnh nạp.'
        }
    }
    Write-Ok 'Nạp xong'
}

# Khởi động lại bo và đọc log khởi động qua Serial
function Read-BootLog([string]$ComPort, [int]$Seconds = 8) {
    $sp = New-Object System.IO.Ports.SerialPort $ComPort, 115200, ([IO.Ports.Parity]::None), 8, ([IO.Ports.StopBits]::One)
    $sp.DtrEnable = $false
    $sp.RtsEnable = $false
    $sp.Encoding = [Text.Encoding]::UTF8
    $sp.Open()
    try {
        # Mạch tự reset của CYD: bật RTS kéo chân EN xuống (reset), DTR để yên nên chạy bình thường
        $sp.RtsEnable = $true
        Start-Sleep -Milliseconds 150
        $sp.RtsEnable = $false
        $log = New-Object Text.StringBuilder
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            $chunk = $sp.ReadExisting()
            if ($chunk) { [void]$log.Append($chunk) }
            if ($log.ToString() -match '\[CYD\] San sang') { break }
            Start-Sleep -Milliseconds 100
        }
        return $log.ToString()
    } finally {
        $sp.Close()
    }
}

# Trả về env đúng nếu bo cần env khác env đang nạp, ngược lại trả về $null
function Invoke-Detect([string]$ComPort, [string]$CurrentEnv) {
    Write-Step 'Kiểm tra loại màn hình trên bo'
    $log = Read-BootLog $ComPort
    $m = [regex]::Match($log, 'CYD_PANEL=(\w+) id=([0-9A-Fa-f]+) build=(\w+)')
    if (-not $m.Success) {
        Write-Note 'Không đọc được dòng CYD_PANEL (bo chưa chạy firmware CYD Deck hoặc không khởi động). Log cuối:'
        ($log -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 12) | ForEach-Object { Write-Host "    $_" }
        return $null
    }
    $panel = $m.Groups[1].Value
    Write-Host "  Chip màn hình: $panel (ID $($m.Groups[2].Value)); firmware đang build cho: $($m.Groups[3].Value)"
    if ($panel -eq 'ST7789') { $wanted = 'cyd2usb' }
    elseif ($panel -eq 'ILI9341') { $wanted = 'cyd' }
    elseif ($panel -eq 'ILI9341_INV') {
        $wanted = 'cyd'
        Write-Host '  Màn IPS cần đảo màu: firmware tự bật, không cần đổi env.'
    }
    else {
        Write-Note 'Không tự nhận ra chip màn. Nhìn màn hình: nền xanh đậm + chữ trắng là đúng.'
        Write-Note 'Màu bị đảo: thêm -DCYD_INVERT=1 vào env cyd trong platformio.ini (bo 2 cổng USB thì thử -Env cyd2usb).'
        return $null
    }
    Set-Content -Path $BoardEnvFile -Value $wanted -Encoding ASCII
    if ($wanted -ne $CurrentEnv) {
        Write-Note "Bo này cần env $wanted (đã lưu vào tools\board-env.txt)."
        return $wanted
    }
    Write-Ok "Đúng loại màn, env $wanted (đã lưu vào tools\board-env.txt)"
    return $null
}

function Invoke-Monitor([string]$TargetEnv, [string]$ComPort) {
    Write-Step "Serial monitor $ComPort (Ctrl+C để thoát)"
    & $Pio device monitor -d $ProjectDir -e $TargetEnv -p $ComPort
}

# Đọc các dòng [SHOT] (bản -DCYD_PERF_DEMO=3, src/deck/shot.cpp) ra ảnh, so hai lần chụp. Viết bằng C# cho nhanh:
# mỗi ảnh 76.800 điểm ảnh.
$ShotCode = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class CydShot {
    public const int W = 320, H = 240;

    // Số dòng hỏng của từng ảnh ở lần Parse gần nhất (dòng không đủ 320 điểm, ví dụ log khác chen vào giữa dòng)
    public static Dictionary<string, int> BadRows = new Dictionary<string, int>();

    // tên ảnh ("main-0": trang main, lượt 0 = theme lúc bắt đầu) -> điểm ảnh RGB565
    public static Dictionary<string, ushort[]> Parse(string path) {
        var shots = new Dictionary<string, ushort[]>();
        BadRows = new Dictionary<string, int>();
        ushort[] current = null;
        string key = null;
        foreach (var raw in File.ReadLines(path)) {
            int at = raw.IndexOf("[SHOT] ");
            if (at < 0) continue;
            string body = raw.Substring(at + 7).Trim();
            if (body.StartsWith("begin ")) {
                var parts = body.Split(' ');
                if (parts.Length < 3) continue;
                key = parts[1] + "-" + parts[2];
                current = new ushort[W * H];
                BadRows[key] = 0;
            } else if (body.StartsWith("end ")) {
                if (current != null) shots[key] = current;
                current = null;
            } else if (current != null && body.Length > 0 && char.IsDigit(body[0])) {
                var parts = body.Split(' ');
                int y, x = 0;
                if (!int.TryParse(parts[0], out y) || y < 0 || y >= H) continue;
                bool bad = false;
                for (int i = 1; i < parts.Length; i++) {
                    var run = parts[i].Split(':');
                    int count;
                    ushort color;
                    if (run.Length != 2 ||
                        !int.TryParse(run[0], System.Globalization.NumberStyles.HexNumber, null, out count) ||
                        !ushort.TryParse(run[1], System.Globalization.NumberStyles.HexNumber, null, out color)) {
                        bad = true;
                        break;
                    }
                    for (int k = 0; k < count && x < W; k++) current[y * W + x++] = color;
                }
                if (bad || x != W) BadRows[key]++;
            }
        }
        return shots;
    }

    static void Put(byte[] bytes, int offset, ushort c) {
        bytes[offset] = (byte)((c & 31) * 255 / 31);
        bytes[offset + 1] = (byte)(((c >> 5) & 63) * 255 / 63);
        bytes[offset + 2] = (byte)(((c >> 11) & 31) * 255 / 31);
    }

    // other != null: ảnh so với lần chụp khác, chỗ khác tô đỏ, chỗ giống làm nhạt
    public static void Save(ushort[] px, ushort[] other, string path) {
        using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb)) {
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            var bytes = new byte[data.Stride * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    int o = y * data.Stride + x * 3, i = y * W + x;
                    Put(bytes, o, px[i]);
                    if (other == null) continue;
                    if (other[i] != px[i]) { bytes[o] = 0; bytes[o + 1] = 0; bytes[o + 2] = 255; }
                    else for (int k = 0; k < 3; k++) bytes[o + k] = (byte)((bytes[o + k] + 510) / 3);
                }
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            bmp.UnlockBits(data);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    public static int Differ(ushort[] a, ushort[] b) {
        int count = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) count++;
        return count;
    }
}
'@

function Invoke-Shot([string]$TargetEnv, [string]$ComPort) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outDir = Join-Path $ProjectDir "shots\$stamp"
    $buildDir = Join-Path $env:TEMP "cyd-shot-build-$stamp"
    $flags = '-DCYD_PERF=1 -DCYD_PERF_DEMO=3 -DSPI_READ_FREQUENCY=6000000'
    if ($ExtraFlags) { $flags += " $ExtraFlags" }
    Write-Step "Build và nạp bản chụp màn hình ($flags)"
    $env:PLATFORMIO_BUILD_FLAGS = $flags
    $env:PLATFORMIO_BUILD_DIR = $buildDir
    try {
        & $Pio run -d $ProjectDir -e $TargetEnv -t upload --upload-port $ComPort
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Build hoặc nạp bản chụp lỗi, xem thông báo phía trên.' }
    } finally {
        Remove-Item Env:PLATFORMIO_BUILD_FLAGS, Env:PLATFORMIO_BUILD_DIR -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    }

    Write-Step 'Chụp (bo tự mở từng cảnh mẫu, ~2 phút)'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $logPath = Join-Path $outDir 'serial.log'
    $sp = New-Object System.IO.Ports.SerialPort $ComPort, 115200, ([IO.Ports.Parity]::None), 8, ([IO.Ports.StopBits]::One)
    $sp.DtrEnable = $false
    $sp.RtsEnable = $false
    $sp.Encoding = [Text.Encoding]::UTF8
    $sp.ReadBufferSize = 1048576
    $sp.Open()
    $log = New-Object Text.StringBuilder
    try {
        $sp.RtsEnable = $true  # khởi động lại bo (xem Read-BootLog)
        Start-Sleep -Milliseconds 150
        $sp.RtsEnable = $false
        $deadline = (Get-Date).AddMinutes(6)
        $tail = ''
        $shots = 0
        $script:ShotTotal = 0
        while ((Get-Date) -lt $deadline) {
            $chunk = $sp.ReadExisting()
            if ($chunk) {
                [void]$log.Append($chunk)
                $tail = $tail + $chunk
                $cut = $tail.LastIndexOf("`n")
                if ($cut -ge 0) {  # chỉ xét các dòng đã trọn
                    $lines = $tail.Substring(0, $cut + 1)
                    $tail = $tail.Substring($cut + 1)
                    $shots += ([regex]::Matches($lines, '\[SHOT\] end ')).Count
                    $total = [regex]::Match($lines, '\[SHOT\] tong (\d+)')
                    if ($total.Success) { $script:ShotTotal = [int]$total.Groups[1].Value }
                    if ($lines.Contains('[SHOT] xong')) { break }
                    Write-Progress -Activity 'Chụp màn hình' -Status "$shots/$($script:ShotTotal) ảnh"
                }
            }
            Start-Sleep -Milliseconds 20
        }
    } finally {
        $sp.Close()
        [IO.File]::WriteAllText($logPath, $log.ToString(), [Text.Encoding]::UTF8)
        Write-Progress -Activity 'Chụp màn hình' -Completed
    }

    try {  # đọc ảnh lỗi thế nào cũng phải nạp lại bản thường (finally)
        Add-Type -TypeDefinition $ShotCode -ReferencedAssemblies System.Drawing
        $images = [CydShot]::Parse($logPath)
        foreach ($name in $images.Keys) {
            [CydShot]::Save($images[$name], $null, (Join-Path $outDir "$name.png"))
            $bad = [CydShot]::BadRows[$name]
            if ($bad -gt 0) { Write-Note "$name có $bad dòng hỏng (log khác chen vào), chỗ đó hiện màu đen" }
        }
        if ($script:ShotTotal -eq 0 -or $images.Count -lt $script:ShotTotal) {
            Write-Note "Chỉ chụp được $($images.Count)/$($script:ShotTotal) ảnh, xem $logPath"
        } else { Write-Ok "$($images.Count) ảnh trong $outDir" }

        if ($Compare) {
            $compareDir = if ([IO.Path]::IsPathRooted($Compare)) { $Compare } else { Join-Path $ProjectDir $Compare }
            $oldLog = Join-Path $compareDir 'serial.log'
            if (-not (Test-Path $oldLog)) { Write-Note "Không thấy $oldLog để so" }
            else {
                $old = [CydShot]::Parse($oldLog)
                $differ = 0
                foreach ($name in ($images.Keys | Sort-Object)) {
                    if (-not $old.ContainsKey($name)) { continue }
                    $count = [CydShot]::Differ($old[$name], $images[$name])
                    if ($count -eq 0) { Write-Host "    $name giống hệt" }
                    else {
                        $differ++
                        [CydShot]::Save($images[$name], $old[$name], (Join-Path $outDir "$name-khac.png"))
                        Write-Note "$name khác $count điểm ảnh (xem $name-khac.png)"
                    }
                }
                if ($differ -eq 0) { Write-Ok "Giống hệt lần chụp $Compare" }
            }
        }
    } finally {
        Write-Step 'Nạp lại bản thường'
        Invoke-Build $TargetEnv
        Invoke-Flash $TargetEnv $ComPort
    }
}

function Invoke-Doctor {
    Write-Step 'Kiểm tra môi trường'
    $ver = & $Pio --version
    Write-Ok "PlatformIO: $ver"

    $ports = @(Get-SerialPorts)
    if ($ports.Count -gt 0) {
        foreach ($p in $ports) { Write-Ok "Cổng bo: $($p.Name)" }
    } else {
        $noDriver = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match $UsbSerialIds -and $_.Status -ne 'OK' })
        if ($noDriver.Count -gt 0) { Write-Note "Bo đã cắm nhưng thiếu driver CH340: cài CH341SER.EXE từ $DriverUrl" }
        else { Write-Note 'Chưa thấy bo (cắm cáp USB có truyền dữ liệu).' }
    }

    Write-Ok "Env đang dùng: $(Get-BoardEnv)"
}

# ---------------------------------------------------------------------------
# Chạy lệnh
# ---------------------------------------------------------------------------
$Pio = Get-Pio
$TargetEnv = Get-BoardEnv

if ($Command -in @('flash', 'detect', 'monitor', 'shot', 'all')) { Suspend-Agent }
try {
    switch ($Command) {
        'doctor' { Invoke-Doctor }
        'build' { Invoke-Build $TargetEnv }
        'flash' { Invoke-Flash $TargetEnv (Find-CydPort) }
        'detect' { $null = Invoke-Detect (Find-CydPort) $TargetEnv }
        'monitor' { Invoke-Monitor $TargetEnv (Find-CydPort) }
        'clean' { & $Pio run -d $ProjectDir -e $TargetEnv -t clean }
        'shot' { Invoke-Shot $TargetEnv (Find-CydPort) }
        'all' {
            $com = Find-CydPort
            Invoke-Build $TargetEnv
            Invoke-Flash $TargetEnv $com
            $fixed = Invoke-Detect $com $TargetEnv
            if ($fixed) {
                $TargetEnv = $fixed
                Invoke-Build $TargetEnv
                Invoke-Flash $TargetEnv $com
                $null = Invoke-Detect $com $TargetEnv
            }
            if (-not $NoMonitor) { Invoke-Monitor $TargetEnv $com }
        }
    }
} finally {
    Resume-Agent
}
