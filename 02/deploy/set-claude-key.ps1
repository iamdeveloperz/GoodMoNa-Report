<#
.SYNOPSIS
  CS 안내 도우미의 AI 답변을 Claude API로 켜거나 끕니다. API 키는 이 창에서 직접 붙여넣습니다(채팅·리포에 남지 않음).

.DESCRIPTION
  데이터 폴더(기본 C:\GoodMoNa\02\data)의 appsettings.local.json에 App:Assistant 설정을 쓰고,
  그 파일은 SYSTEM(서비스)과 Administrators만 읽을 수 있게 권한을 줄인 뒤 GoodMoNa02 서비스를 다시 시작합니다.
  이 파일은 배포(install.ps1)가 덮어쓰지 않습니다.

  켜기:  powershell -ExecutionPolicy Bypass -File deploy\set-claude-key.ps1
  모델:  ... -Model claude-opus-5-5   (비우면 claude-opus-5-5)
  끄기:  ... -Off
#>
param(
    [string]$DataDir = "C:\GoodMoNa\02\data",
    [string]$Model = "",
    [switch]$Off
)
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    $relaunch = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"", "-DataDir", "`"$DataDir`"")
    if ($Model) { $relaunch += @("-Model", $Model) }
    if ($Off) { $relaunch += "-Off" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunch
    return
}

if (-not (Test-Path $DataDir)) { throw "데이터 폴더가 없습니다: $DataDir (먼저 deploy\install-all.ps1로 설치하세요)" }
$file = Join-Path $DataDir "appsettings.local.json"

# 기존 설정을 읽어 App:Assistant만 바꿉니다.
$config = if (Test-Path $file) { Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json } else { New-Object PSObject }
if (-not $config.PSObject.Properties["App"]) { $config | Add-Member -NotePropertyName App -NotePropertyValue (New-Object PSObject) }

if ($Off) {
    $assistant = [ordered]@{ Mode = "Off" }
    Write-Host "AI 답변을 끕니다(검색 결과만)." -ForegroundColor Cyan
} else {
    Write-Host "Claude API 키를 붙여넣고 Enter를 누르세요(화면에 보이지 않습니다). 키는 platform.claude.com 콘솔의 API Keys에서 만듭니다." -ForegroundColor Cyan
    $secure = Read-Host "API 키" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if (-not $key.StartsWith("sk-ant-")) { throw "Claude API 키 형식(sk-ant-…)이 아닙니다. 아무것도 바꾸지 않았습니다." }
    $assistant = [ordered]@{ Mode = "Claude"; Model = $Model; ApiKey = $key; TimeoutSeconds = 60; MaxPassages = 4 }
}
$config.App | Add-Member -NotePropertyName Assistant -NotePropertyValue $assistant -Force
$config | ConvertTo-Json -Depth 6 | Set-Content $file -Encoding UTF8

# 키가 든 파일: 서비스(SYSTEM)와 관리자만 (S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators)
icacls $file /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null
Write-Host "저장: $file (SYSTEM·Administrators만 읽기)" -ForegroundColor Green

Restart-Service GoodMoNa02
(Get-Service GoodMoNa02).WaitForStatus("Running", "00:00:30")
Start-Sleep -Seconds 3
try {
    $status = Invoke-RestMethod "http://localhost:5002/healthz"
    Write-Host "GoodMoNa02 다시 시작: $($status.status). 인트라넷 CS 도우미 화면 위에 'AI 답변 켜짐'이 보이면 됩니다." -ForegroundColor Green
} catch { Write-Host "서비스는 시작했지만 응답 확인에 실패했습니다: $_" -ForegroundColor Yellow }
