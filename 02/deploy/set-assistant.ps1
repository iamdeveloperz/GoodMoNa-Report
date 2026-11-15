<#
.SYNOPSIS
  CS 안내 도우미의 AI 답변 방식을 바꿉니다: 서버 PC 로컬 모델(Ollama) · Claude API · 끄기(검색만).

.DESCRIPTION
  데이터 폴더(기본 C:\GoodMoNa\02\data)의 appsettings.local.json에 App:Assistant 설정을 쓰고 GoodMoNa02 서비스를 다시 시작합니다.
  이 파일은 배포(install.ps1)가 덮어쓰지 않습니다.

  로컬:   powershell -ExecutionPolicy Bypass -File deploy\set-assistant.ps1 -Mode Ollama -Model qwen3:4b-instruct
  Claude: powershell -ExecutionPolicy Bypass -File deploy\set-assistant.ps1 -Mode Claude   (API 키는 열린 관리자 창에 직접 붙여넣음)
  끄기:   powershell -ExecutionPolicy Bypass -File deploy\set-assistant.ps1 -Mode Off

  Claude 키가 든 파일은 SYSTEM(서비스)과 Administrators만 읽을 수 있게 권한을 줄입니다.
#>
param(
    [ValidateSet("Ollama", "Claude", "Off")]
    [string]$Mode = "Ollama",
    [string]$Model = "",
    [string]$DataDir = "C:\GoodMoNa\02\data",
    [string]$OllamaUrl = "http://localhost:11434"
)
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "관리자 권한이 필요합니다. UAC 창에서 '예'를 눌러 주세요." -ForegroundColor Yellow
    $relaunch = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"", "-Mode", $Mode, "-DataDir", "`"$DataDir`"", "-OllamaUrl", $OllamaUrl)
    if ($Model) { $relaunch += @("-Model", $Model) }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunch
    return
}

if (-not (Test-Path $DataDir)) { throw "데이터 폴더가 없습니다: $DataDir (먼저 deploy\install-all.ps1로 설치하세요)" }
$file = Join-Path $DataDir "appsettings.local.json"

# 기존 설정을 읽어 App:Assistant만 바꿉니다.
$config = if (Test-Path $file) { Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json } else { New-Object PSObject }
if (-not $config.PSObject.Properties["App"]) { $config | Add-Member -NotePropertyName App -NotePropertyValue (New-Object PSObject) }

$hasSecret = $false
switch ($Mode) {
    "Off" {
        $assistant = [ordered]@{ Mode = "Off" }
        Write-Host "AI 답변을 끕니다(검색 결과만)." -ForegroundColor Cyan
    }
    "Ollama" {
        if (-not $Model) { throw "-Model 이 필요합니다 (예: qwen3:4b-instruct)." }
        try {
            $tags = Invoke-RestMethod "$OllamaUrl/api/tags" -TimeoutSec 5
            if (-not ($tags.models | Where-Object { $_.name -eq $Model -or $_.name -eq "$Model`:latest" })) {
                Write-Host "주의: Ollama에 '$Model' 모델이 아직 없습니다. 'ollama pull $Model' 후 쓰세요(그 전에는 검색 결과만 나옵니다)." -ForegroundColor Yellow
            }
        } catch {
            Write-Host "주의: Ollama($OllamaUrl)에 연결하지 못했습니다. Ollama가 켜져 있어야 AI 답변이 나옵니다(꺼져 있으면 검색 결과만)." -ForegroundColor Yellow
        }
        $assistant = [ordered]@{ Mode = "Ollama"; Model = $Model; OllamaUrl = $OllamaUrl; TimeoutSeconds = 60; MaxPassages = 4 }
    }
    "Claude" {
        Write-Host "Claude API 키를 붙여넣고 Enter를 누르세요(화면에 보이지 않습니다). 키는 platform.claude.com 콘솔의 API Keys에서 만듭니다." -ForegroundColor Cyan
        $secure = Read-Host "API 키" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        if (-not $key.StartsWith("sk-ant-")) { throw "Claude API 키 형식(sk-ant-…)이 아닙니다. 아무것도 바꾸지 않았습니다." }
        $assistant = [ordered]@{ Mode = "Claude"; Model = $Model; ApiKey = $key; TimeoutSeconds = 60; MaxPassages = 4 }
        $hasSecret = $true
    }
}
$config.App | Add-Member -NotePropertyName Assistant -NotePropertyValue $assistant -Force
$config | ConvertTo-Json -Depth 6 | Set-Content $file -Encoding UTF8

if ($hasSecret) {
    # 키가 든 파일: 서비스(SYSTEM)와 관리자만 (S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators)
    icacls $file /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null
    Write-Host "저장: $file (SYSTEM·Administrators만 읽기)" -ForegroundColor Green
} else {
    Write-Host "저장: $file" -ForegroundColor Green
}

Restart-Service GoodMoNa02
(Get-Service GoodMoNa02).WaitForStatus("Running", "00:00:30")
Start-Sleep -Seconds 3
try {
    $null = Invoke-RestMethod "http://localhost:5002/healthz"
    Write-Host "GoodMoNa02를 다시 시작했습니다. CS 도우미 화면 위에 AI 답변 상태가 표시됩니다." -ForegroundColor Green
} catch { Write-Host "서비스는 시작했지만 응답 확인에 실패했습니다: $_" -ForegroundColor Yellow }
