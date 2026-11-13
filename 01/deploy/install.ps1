<#
.SYNOPSIS
  GoodMoNa 01(보고 앱)을 이 PC에 Windows 서비스로 설치하고, Cloudflare Tunnel로 https://<Hostname> 에 공개합니다.

.DESCRIPTION
  다시 실행해도 됩니다(코드를 고친 뒤 재배포할 때도 이 스크립트 하나로). 하는 일:
    1. web 빌드 → 서버 publish (C:\GoodMoNa\01\app)
    2. 서버를 Windows 서비스 "GoodMoNa01"로 등록 (부팅 시 자동 시작, 죽으면 자동 재시작)
    3. Cloudflare named tunnel 생성 + DNS(CNAME) 연결 + 터널을 서비스 "GoodMoNa01Tunnel"로 등록
    4. 외부 주소로 /healthz 확인

  먼저 한 번 `cloudflared tunnel login`으로 hsdevs.com을 승인해 두어야 합니다.
  관리자 권한이 없으면 UAC 창을 띄워 스스로 다시 실행합니다.
#>
param(
    [string]$Hostname = "goodmona.hsdevs.com",
    [string]$TunnelName = "goodmona",
    [string]$InstallRoot = "C:\GoodMoNa\01",
    [int]$Port = 5001,
    [string]$ModelPath = "C:\GoodMoNa\models\ggml-large-v3-turbo.bin",
    [switch]$SkipTunnel
)

$ErrorActionPreference = "Stop"

# ---------- 관리자 권한으로 다시 실행 ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"",
        "-Hostname", $Hostname, "-TunnelName", $TunnelName, "-InstallRoot", "`"$InstallRoot`"", "-Port", $Port, "-ModelPath", "`"$ModelPath`"")
    if ($SkipTunnel) { $argList += "-SkipTunnel" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    return
}

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "실패: $text" -ForegroundColor Red; exit 1 }
function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { Fail "$what (종료 코드 $LASTEXITCODE)" }
}
# 이미 있는 서비스의 실행 경로를 바꿉니다. sc.exe config binPath= 는 Windows PowerShell 5.1이
# 경로 속 따옴표를 망가뜨려(종료 코드 1639) 쓰지 않고 레지스트리를 직접 고칩니다. 다음 시작부터 반영.
function Set-ServicePath([string]$name, [string]$imagePath) {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name ImagePath -Value $imagePath
    Set-Service $name -StartupType Automatic
}

$root = Split-Path $PSScriptRoot -Parent          # ...\01
$appDir = Join-Path $InstallRoot "app"
$dataDir = Join-Path $InstallRoot "data"
$tunnelDir = Join-Path $InstallRoot "cloudflared"
$appService = "GoodMoNa01"
$tunnelService = "GoodMoNa01Tunnel"

# ---------- 준비물 확인 ----------
Step "준비물 확인"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
foreach ($tool in "dotnet", "npm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool 이(가) 설치되어 있지 않습니다." }
}
$cloudflared = $null
if (-not $SkipTunnel) {
    $cmd = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($cmd) { $cloudflared = $cmd.Source }
    elseif (Test-Path "C:\Program Files (x86)\cloudflared\cloudflared.exe") { $cloudflared = "C:\Program Files (x86)\cloudflared\cloudflared.exe" }
    else { Fail "cloudflared가 없습니다. winget install Cloudflare.cloudflared 로 설치하세요." }

    $cert = Join-Path $env:USERPROFILE ".cloudflared\cert.pem"
    if (-not (Test-Path $cert)) { Fail "Cloudflare 로그인이 필요합니다: cloudflared tunnel login (브라우저에서 hsdevs.com 선택)" }
}
Write-Host "설치 위치: $InstallRoot / 외부 주소: https://$Hostname"

# ---------- Whisper 모델 ----------
Step "Whisper 모델 확인: $ModelPath"
if (-not (Test-Path $ModelPath)) {
    $name = Split-Path $ModelPath -Leaf
    Write-Host "모델이 없어 내려받습니다 (약 1.6GB): huggingface.co/ggerganov/whisper.cpp/$name"
    New-Item -ItemType Directory -Force (Split-Path $ModelPath -Parent) | Out-Null
    Invoke-Native "모델 다운로드" { curl.exe -L --fail --retry 3 -o "$ModelPath.part" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$name" }
    Move-Item -Force "$ModelPath.part" $ModelPath
}
Write-Host ("모델 크기: {0:N0} MB" -f ((Get-Item $ModelPath).Length / 1MB))

# ---------- 빌드 ----------
Step "화면(web) 빌드"
Push-Location (Join-Path $root "web")
try {
    Invoke-Native "npm ci" { npm ci --no-audit --no-fund }
    Invoke-Native "npm run build" { npm run build }
}
finally { Pop-Location }

# 터널은 멈추지 않습니다: 뒤 단계에서 실패해도 외부 접속이 끊긴 채로 남지 않게 (서버만 잠깐 502).
Step "기존 서버 서비스 정지"
$svc = Get-Service $appService -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") { Stop-Service $appService -Force; $svc.WaitForStatus("Stopped", "00:00:30") }

Step "서버 publish → $appDir"
New-Item -ItemType Directory -Force $appDir, $dataDir, $tunnelDir | Out-Null
Invoke-Native "dotnet publish" { dotnet publish (Join-Path $root "src\GoodMoNa.Server") -c Release -o $appDir --nologo }

# 운영 설정: 데이터 폴더·외부 주소. (예시 데이터는 실제 환자 데이터를 넣기 전까지만 켜 두세요)
$settings = [ordered]@{
    Kestrel = @{ Endpoints = @{ Report = @{ Url = "http://localhost:$Port" } } }
    App     = [ordered]@{ DataRoot = $dataDir; PublicBaseUrl = "https://$Hostname"; SeedDemoData = $true; Whisper = @{ ModelPath = $ModelPath } }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $appDir "appsettings.Production.json") -Encoding UTF8

# ---------- 서버 서비스 ----------
Step "서버 서비스 등록: $appService"
$exe = Join-Path $appDir "GoodMoNa.Server.exe"
if (-not (Get-Service $appService -ErrorAction SilentlyContinue)) {
    New-Service -Name $appService -DisplayName "GoodMoNa 01 Report" -Description "GoodMoNa PMS 보고 앱 (localhost:$Port)" `
        -BinaryPathName "`"$exe`"" -StartupType Automatic | Out-Null
}
else {
    Set-ServicePath $appService "`"$exe`""
}
Invoke-Native "sc failure" { sc.exe failure $appService reset= 86400 actions= restart/5000/restart/5000/restart/30000 }
Start-Service $appService

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try { if ((Invoke-RestMethod "http://localhost:$Port/healthz" -TimeoutSec 2).status -eq "ok") { $ok = $true; break } } catch { Start-Sleep 1 }
}
if (-not $ok) { Fail "서버가 http://localhost:$Port 에서 응답하지 않습니다. 이벤트 뷰어 > Windows 로그 > Application 을 확인하세요." }
Write-Host "서버 정상: http://localhost:$Port" -ForegroundColor Green

if ($SkipTunnel) { Write-Host "`n터널은 건너뛰었습니다."; exit 0 }

# ---------- Cloudflare Tunnel ----------
Step "터널 준비: $TunnelName"
$tunnels = & $cloudflared tunnel list --name $TunnelName --output json | ConvertFrom-Json
# 지워지지 않은 터널은 deleted_at이 비어 있거나 "0001-01-01..."(Go의 zero time)로 옵니다.
$tunnel = $tunnels | Where-Object { $_.name -eq $TunnelName -and (-not $_.deleted_at -or "$($_.deleted_at)".StartsWith("0001-")) } | Select-Object -First 1
if (-not $tunnel) {
    Invoke-Native "tunnel create" { & $cloudflared tunnel create $TunnelName }
    $tunnel = (& $cloudflared tunnel list --name $TunnelName --output json | ConvertFrom-Json) | Select-Object -First 1
}
if (-not $tunnel.id) { Fail "터널 '$TunnelName'을(를) 찾지 못했습니다." }
$id = $tunnel.id
$credSrc = Join-Path $env:USERPROFILE ".cloudflared\$id.json"
$credDst = Join-Path $tunnelDir "$id.json"
if (Test-Path $credSrc) { Copy-Item $credSrc $credDst -Force }
elseif (-not (Test-Path $credDst)) { Fail "터널 자격 증명($id.json)을 찾을 수 없습니다. 다른 PC에서 만든 터널이면 그 파일을 $tunnelDir 에 복사하세요." }

# 서비스(LocalSystem)는 사용자 폴더를 못 보므로 설정·자격 증명을 설치 폴더에 둡니다.
$config = Join-Path $tunnelDir "config.yml"
@"
tunnel: $id
credentials-file: '$credDst'
logfile: '$(Join-Path $tunnelDir "cloudflared.log")'
no-autoupdate: true
ingress:
  - hostname: $Hostname
    service: http://localhost:$Port
  - service: http_status:404
"@ | Set-Content $config -Encoding ASCII
Invoke-Native "ingress validate" { & $cloudflared tunnel --config $config ingress validate }

Step "DNS 연결: $Hostname → 터널"
& $cloudflared tunnel route dns $TunnelName $Hostname
if ($LASTEXITCODE -ne 0) {
    Write-Host "DNS 레코드를 만들지 못했습니다. 이미 $Hostname 레코드가 있다면 Cloudflare 대시보드에서 지우거나 CNAME $id.cfargotunnel.com 으로 바꾸세요." -ForegroundColor Yellow
}

Step "터널 서비스 등록: $tunnelService"
$bin = "`"$cloudflared`" --config `"$config`" tunnel run"
if (-not (Get-Service $tunnelService -ErrorAction SilentlyContinue)) {
    New-Service -Name $tunnelService -DisplayName "GoodMoNa 01 Tunnel" -Description "Cloudflare Tunnel: https://$Hostname → localhost:$Port" `
        -BinaryPathName $bin -StartupType Automatic | Out-Null
}
else {
    Set-ServicePath $tunnelService $bin
}
Invoke-Native "sc failure" { sc.exe failure $tunnelService reset= 86400 actions= restart/5000/restart/5000/restart/30000 }
Restart-Service $tunnelService -Force   # 바뀐 config.yml 반영 (멈춰 있었으면 시작)

# ---------- 외부 확인 ----------
Step "외부 접속 확인: https://$Hostname/healthz (DNS 반영에 1~2분 걸릴 수 있음)"
$ok = $false
for ($i = 0; $i -lt 40; $i++) {
    try { if ((Invoke-RestMethod "https://$Hostname/healthz" -TimeoutSec 5).status -eq "ok") { $ok = $true; break } } catch { Start-Sleep 3 }
}
if ($ok) {
    Write-Host "`n완료! 폰에서 https://$Hostname 을 여세요." -ForegroundColor Green
}
else {
    Write-Host "`n서비스는 켜졌지만 아직 외부에서 응답이 없습니다. 몇 분 뒤 https://$Hostname/healthz 를 다시 열어 보세요. 로그: $tunnelDir\cloudflared.log" -ForegroundColor Yellow
}
