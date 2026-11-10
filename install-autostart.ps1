$ErrorActionPreference = 'Stop'
$filterName   = 'MuMuAudioRouter_Start'
$consumerName = 'MuMuAudioRouter_Consumer'
$query = "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'MuMuNxMain.exe' OR ProcessName = 'MuMuNxDevice.exe'"
$exe = 'E:\MuMu实例音频路由\MuMuAudioRouter.exe'
$taskName = 'MuMuAudioRouter'
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

# 清理旧订阅
Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue |
  Where-Object { $_.Filter -match $filterName -or $_.Consumer -match $consumerName } |
  ForEach-Object { Remove-WmiObject -InputObject $_ -ErrorAction SilentlyContinue }
Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $consumerName } | Remove-WmiObject -ErrorAction SilentlyContinue
Get-WmiObject -Namespace root\subscription -Class __EventFilter -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $filterName } | Remove-WmiObject -ErrorAction SilentlyContinue

# 1) 计划任务：只当“桥”用，不设开机/登录触发器，靠 /run 按需在【用户交互会话】里启动
#    原因：WMI 的 CommandLineEventConsumer 会把进程放进 Session 0，那里看不到桌面也没有音频设备
$exeQuoted = '"' + $exe + '"'
schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
& schtasks.exe /create /tn $taskName /tr $exeQuoted /sc once /st 00:00 /ru $user /it /rl LIMITED /f | Out-Null
if ($LASTEXITCODE -ne 0) { throw ('创建计划任务失败（schtasks exit ' + $LASTEXITCODE + '）') }

# 2) WMI 事件过滤器：MuMu 的进程一出现就触发
$filter = Set-WmiInstance -Namespace root\subscription -Class __EventFilter -Arguments @{
  Name = $filterName
  EventNamespace = 'root\cimv2'
  QueryLanguage = 'WQL'
  Query = $query
}

# 3) 消费者改成 schtasks：任务调度器会把它放进已登录用户的会话，而不是 Session 0
#    CommandLineEventConsumer 没有 Arguments 属性，只有 CommandLineTemplate
$consumer = Set-WmiInstance -Namespace root\subscription -Class CommandLineEventConsumer -Arguments @{
  Name = $consumerName
  CommandLineTemplate = 'C:\Windows\System32\schtasks.exe /run /tn ' + $taskName
}

# 4) 绑定
Set-WmiInstance -Namespace root\subscription -Class __FilterToConsumerBinding -Arguments @{
  Filter = $filter
  Consumer = $consumer
} | Out-Null

Write-Host 'OK: 已安装（计划任务会话桥 + WMI 触发）'
Write-Host ('    任务名: ' + $taskName + '  用户: ' + $user)

