# 관리자 PowerShell에서 실행합니다. 시스템 변경은 이 스크립트를 실행할 때만 발생합니다.
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '관리자 PowerShell에서 실행하세요.'
}

$source = Join-Path $PSScriptRoot 'bin\Release\net9.0-windows'
$destination = Join-Path $env:ProgramFiles 'RoutineHelper'
$exe = Join-Path $destination 'RoutineHelper.exe'
if (-not (Test-Path (Join-Path $source 'RoutineHelper.exe'))) {
    throw '먼저 dotnet build -c Release를 실행하세요.'
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-Process -Name 'RoutineHelper' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    Stop-Process -Force
Get-ChildItem -LiteralPath $source -File | ForEach-Object {
    if ($_.Name -ne 'config.json' -or -not (Test-Path (Join-Path $destination 'config.json'))) {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

$taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew
$trayAction = New-ScheduledTaskAction -Execute $exe
$trayTrigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
Register-ScheduledTask -TaskName 'RoutineHelper-Tray' -Action $trayAction -Trigger $trayTrigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null

$repairAction = New-ScheduledTaskAction -Execute $exe -Argument '--reconcile'
$repairTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$repairSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName 'RoutineHelper-Reconcile' -Action $repairAction -Trigger $repairTrigger -Principal $taskPrincipal -Settings $repairSettings -Force | Out-Null

Start-ScheduledTask -TaskName 'RoutineHelper-Tray'
Write-Host "설치 완료. 설정 파일: $(Join-Path $destination 'config.json')"
