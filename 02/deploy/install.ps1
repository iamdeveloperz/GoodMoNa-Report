<#
.SYNOPSIS
  GoodMoNa 02(인트라넷: 직원 계정·로그인)를 이 PC에 Windows 서비스로 설치합니다.

.DESCRIPTION
  다시 실행해도 됩니다. 하는 일:
    1. web 빌드 → 서버 publish (C:\GoodMoNa\02\app)
    2. 서비스 "GoodMoNa02" 등록 (localhost:5002, 부팅 시 자동 시작, 죽으면 자동 재시작)
    3. http://localhost:5002/healthz 확인

  외부 공개(Cloudflare Tunnel)는 01의 터널이 경로로 나눠 보냅니다:
    /login · /signup · /intranet* · /api/auth/* · /api/admin/* · /.well-known/* → 02, 나머지 → 01
  터널 설정은 01/deploy/install.ps1이 씁니다. 두 개를 한 번에 하려면 리포 맨 위의 deploy/install-all.ps1.

  관리자 권한이 없으면 UAC 창을 띄워 스스로 다시 실행합니다.
#>
param(
    [string]$InstallRoot = "C:\GoodMoNa\02",
    [int]$Port = 5002
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"",
        "-InstallRoot", "`"$InstallRoot`"", "-Port", $Port)
    return
}

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "실패: $text" -ForegroundColor Red; exit 1 }
function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { Fail "$what (종료 코드 $LASTEXITCODE)" }
}
# sc.exe config binPath= 는 PowerShell 5.1에서 따옴표가 깨지므로 레지스트리를 직접 고칩니다. (01과 같은 방식)
function Set-ServicePath([string]$name, [string]$imagePath) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name ImagePath -Value $imagePath
    Set-Service $name -StartupType Automatic
}

$root = Split-Path $PSScriptRoot -Parent          # ...\02
$appDir = Join-Path $InstallRoot "app"
$dataDir = Join-Path $InstallRoot "data"           # SQLite + 서명 키(keys\signing-key.pem) — 리포 밖
$service = "GoodMoNa02"

Step "준비물 확인"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
foreach ($tool in "dotnet", "npm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool 이(가) 설치되어 있지 않습니다." }
}

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
Invoke-Native "dotnet publish" { dotnet publish (Join-Path $root "src\GoodMoNa02.Server") -c Release -o $appDir --nologo }

$settings = [ordered]@{
    Kestrel = @{ Endpoints = @{ App = @{ Url = "http://localhost:$Port" } } }
    App     = [ordered]@{ DataRoot = $dataDir }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $appDir "appsettings.Production.json") -Encoding UTF8

Step "서비스 등록: $service"
$exe = Join-Path $appDir "GoodMoNa02.Server.exe"
if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -DisplayName "GoodMoNa 02 Intranet" -Description "GoodMoNa 인트라넷: 직원 계정·로그인 (localhost:$Port)" `
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
Write-Host "인트라넷 서버 정상: http://localhost:$Port" -ForegroundColor Green

$boot = Invoke-RestMethod "http://localhost:$Port/api/auth/bootstrap"
if ($boot.available) {
    Write-Host "`n아직 관리자 계정이 없습니다. 이 PC의 브라우저에서 http://localhost:$Port/signup 을 열어 첫 관리자(대표원장)를 만드세요." -ForegroundColor Yellow
}
