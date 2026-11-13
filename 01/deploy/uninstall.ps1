<#
.SYNOPSIS
  install.ps1로 만든 Windows 서비스 두 개를 지웁니다. 데이터(C:\GoodMoNa\01\data)와 Cloudflare 터널·DNS는 남겨 둡니다.
#>
$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", "`"$PSCommandPath`"")
    return
}
foreach ($name in "GoodMoNa01Tunnel", "GoodMoNa01") {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -Force -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
        Write-Host "삭제: $name"
    }
}
Write-Host "터널 자체를 지우려면: cloudflared tunnel delete goodmona (DNS 레코드는 Cloudflare 대시보드에서 삭제)"
