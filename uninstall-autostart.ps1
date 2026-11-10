$ErrorActionPreference = 'Stop'
$filterName   = 'MuMuAudioRouter_Start'
$consumerName = 'MuMuAudioRouter_Consumer'
$taskName = 'MuMuAudioRouter'

Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue |
  Where-Object { $_.Filter -match $filterName -or $_.Consumer -match $consumerName } |
  ForEach-Object { Remove-WmiObject -InputObject $_ -ErrorAction SilentlyContinue }
Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $consumerName } | Remove-WmiObject -ErrorAction SilentlyContinue
Get-WmiObject -Namespace root\subscription -Class __EventFilter -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $filterName } | Remove-WmiObject -ErrorAction SilentlyContinue
schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
Write-Host 'OK: 已卸载（WMI 订阅 + 计划任务）'

