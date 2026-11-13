<#
.SYNOPSIS
  02 인트라넷(로그인) → 01 진료(보고·결과 확인 + Cloudflare 터널) 순서로 한 번에 설치·재배포합니다.

.DESCRIPTION
  UAC 승인은 한 번만 하면 됩니다. 각 단계는 02/deploy/install.ps1, 01/deploy/install.ps1과 같습니다.
  02를 먼저 올리는 이유: 01이 로그인 확인에 쓰는 서명 공개키를 02에서 받고, 터널이 로그인 경로를 02로 보내기 때문입니다.
#>
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"")
    return
}

$repo = Split-Path $PSScriptRoot -Parent

Write-Host "`n########## 02 인트라넷 ##########" -ForegroundColor Magenta
& (Join-Path $repo "02\deploy\install.ps1")
if ($LASTEXITCODE -ne 0) { Write-Host "02 설치에 실패해 01은 건너뜁니다." -ForegroundColor Red; exit 1 }

Write-Host "`n########## 01 진료 + 터널 ##########" -ForegroundColor Magenta
& (Join-Path $repo "01\deploy\install.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n모두 완료. 외부: https://goodmona.hsdevs.com/login" -ForegroundColor Green
