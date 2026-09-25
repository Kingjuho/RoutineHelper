# 관리자 PowerShell에서 실행합니다. 이 앱의 작업과 설치 파일만 제거합니다.
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '관리자 PowerShell에서 실행하세요.'
}

$destination = Join-Path $env:ProgramFiles 'RoutineHelper'
$exe = Join-Path $destination 'RoutineHelper.exe'
foreach ($name in @('RoutineHelper-Tray', 'RoutineHelper-Reconcile')) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
    }
}

Get-Process -Name 'RoutineHelper' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    Stop-Process -Force

if (Test-Path $exe) {
    $process = Start-Process -FilePath $exe -ArgumentList '--clear' -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "hosts 정리에 실패했습니다. 로그를 확인하세요: $env:LOCALAPPDATA\RoutineHelper\app.log"
    }
}
if (Test-Path $destination) {
    Remove-Item -LiteralPath $destination -Recurse -Force
}
Write-Host '제거 완료. hosts 관리 구역을 정리했습니다. LocalAppData\RoutineHelper의 기존 파일과 로그는 보존했습니다.'
