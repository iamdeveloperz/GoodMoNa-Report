<#
.SYNOPSIS
  GoodMoNa 03(쇼츠 제작: 촬영본 → 자막·컷·초안 → 검토·승인 → 완성본)을 이 PC에 Windows 서비스로 설치합니다.

.DESCRIPTION
  다시 실행해도 됩니다. 하는 일:
    1. ffmpeg·ffprobe를 C:\GoodMoNa\03\tools로 복사 (서비스 계정은 사용자 PATH를 못 봄)
    2. web 빌드 → 서버 publish (C:\GoodMoNa\03\app)
    3. 서비스 "GoodMoNa03" 등록 (localhost:5003, 부팅 시 자동 시작, 죽으면 자동 재시작)
    4. http://localhost:5003/healthz 확인

  외부 공개는 01의 터널이 경로로 나눠 보냅니다: /shorts · /shorts-assets · /api/shorts/* → 03.
  터널 설정은 01/deploy/install.ps1이 씁니다. 한 번에 하려면 리포 맨 위의 deploy/install-all.ps1 (02 → 03 → 01).

  운영에서 바꿀 설정(AI 모드·Claude 키 등)은 데이터 폴더의 appsettings.local.json에 둡니다(배포가 덮어쓰지 않음).
  처음 설치할 때만 서버 PC Ollama(-AiModel)로 제목·훅 초안을 쓰도록 만들어 둡니다. 끄려면 그 파일의 Mode를 Off로.

  관리자 권한이 없으면 UAC 창을 띄워 스스로 다시 실행합니다.
#>
param(
    [string]$InstallRoot = "C:\GoodMoNa\03",
    [int]$Port = 5003,
    [int]$IntranetPort = 5002,
    [string]$ModelPath = "C:\GoodMoNa\models\ggml-large-v3-turbo.bin",
    [string]$AiModel = "qwen3:4b-instruct"
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"",
        "-InstallRoot", "`"$InstallRoot`"", "-Port", $Port, "-IntranetPort", $IntranetPort, "-ModelPath", "`"$ModelPath`"", "-AiModel", $AiModel)
    return
}

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "실패: $text" -ForegroundColor Red; exit 1 }
function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { Fail "$what (종료 코드 $LASTEXITCODE)" }
}
# sc.exe config binPath= 는 PowerShell 5.1에서 따옴표가 깨지므로 레지스트리를 직접 고칩니다. (01·02와 같은 방식)
function Set-ServicePath([string]$name, [string]$imagePath) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name ImagePath -Value $imagePath
    Set-Service $name -StartupType Automatic
}
# winget 별칭(심볼릭 링크)이면 실제 파일 경로로
function Resolve-Exe([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if (-not $cmd) { return $null }
    $item = Get-Item $cmd.Source
    if ($item.LinkType -and $item.Target) { return @($item.Target)[0] }
    return $item.FullName
}

$root = Split-Path $PSScriptRoot -Parent          # ...\03
$appDir = Join-Path $InstallRoot "app"
$dataDir = Join-Path $InstallRoot "data"           # SQLite + 촬영본·미리보기·완성본 — 리포 밖
$toolsDir = Join-Path $InstallRoot "tools"
$service = "GoodMoNa03"

Step "준비물 확인"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
foreach ($tool in "dotnet", "npm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool 이(가) 설치되어 있지 않습니다." }
}
if (-not (Test-Path $ModelPath)) { Fail "Whisper 모델이 없습니다: $ModelPath (01 설치가 내려받습니다)" }

Step "ffmpeg 복사 → $toolsDir"
New-Item -ItemType Directory -Force $toolsDir | Out-Null
foreach ($name in "ffmpeg", "ffprobe") {
    $src = Resolve-Exe $name
    $dst = Join-Path $toolsDir "$name.exe"
    if ($src) {
        # 서비스가 쓰는 중이면 못 덮어쓰므로 서비스를 먼저 멈춤
        $svc = Get-Service $service -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne "Stopped") { Stop-Service $service -Force; $svc.WaitForStatus("Stopped", "00:00:30") }
        Copy-Item $src $dst -Force
    }
    elseif (-not (Test-Path $dst)) { Fail "$name 이(가) 없습니다. winget install --id Gyan.FFmpeg -e 로 설치하세요." }
}
Write-Host (& (Join-Path $toolsDir "ffmpeg.exe") -hide_banner -version | Select-Object -First 1)

Step "화면(web) 빌드"
Push-Location (Join-Path $root "web")
try {
    Invoke-Native "npm ci" { npm ci --no-audit --no-fund }
    Invoke-Native "npm run build" { npm run build }
}
finally { Pop-Location }

Step "기존 서비스 정지"
$svc = Get-Service $service -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") { Stop-Service $service -Force; $svc.WaitForStatus("Stopped", "00:00:30") }

Step "서버 publish → $appDir"
New-Item -ItemType Directory -Force $appDir, $dataDir | Out-Null
Invoke-Native "dotnet publish" { dotnet publish (Join-Path $root "src\GoodMoNa03.Server") -c Release -o $appDir --nologo }

$settings = [ordered]@{
    Kestrel = @{ Endpoints = @{ Shorts = @{ Url = "http://localhost:$Port" } } }
    App     = [ordered]@{
        DataRoot = $dataDir
        Auth     = @{ Authority = "http://localhost:$IntranetPort" }
        Whisper  = @{ ModelPath = $ModelPath }
        Ffmpeg   = @{ FfmpegPath = (Join-Path $toolsDir "ffmpeg.exe"); FfprobePath = (Join-Path $toolsDir "ffprobe.exe") }
    }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $appDir "appsettings.Production.json") -Encoding UTF8

# 운영 설정(처음 한 번만 만듦 — 이후에는 사람이 고친 값을 그대로 둠)
$local = Join-Path $dataDir "appsettings.local.json"
if (-not (Test-Path $local) -and $AiModel) {
    @{ App = @{ Ai = @{ Mode = "Ollama"; Model = $AiModel } } } | ConvertTo-Json -Depth 5 | Set-Content $local -Encoding UTF8
    Write-Host "AI 초안: 서버 PC Ollama($AiModel) — 바꾸려면 $local"
}

Step "서비스 등록: $service"
$exe = Join-Path $appDir "GoodMoNa03.Server.exe"
if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -DisplayName "GoodMoNa 03 Shorts" -Description "GoodMoNa 쇼츠 제작: 촬영본 → 자막·초안 → 검토·승인 (localhost:$Port)" `
        -BinaryPathName "`"$exe`"" -StartupType Automatic | Out-Null
}
else {
    Set-ServicePath $service "`"$exe`""
}
Invoke-Native "sc failure" { sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/30000 }
Start-Service $service

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try { if ((Invoke-RestMethod "http://localhost:$Port/healthz" -TimeoutSec 2).status -eq "ok") { $ok = $true; break } } catch { Start-Sleep 1 }
}
if (-not $ok) { Fail "서버가 http://localhost:$Port 에서 응답하지 않습니다. 이벤트 뷰어 > Windows 로그 > Application 을 확인하세요." }
Write-Host "쇼츠 서버 정상: http://localhost:$Port/shorts (외부 주소는 01 터널 재배포 후 /shorts)" -ForegroundColor Green
