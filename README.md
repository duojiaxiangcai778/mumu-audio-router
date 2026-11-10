# MuMu 实例音频路由

MuMu Player 12（MuMuNx 内核）多开时，**只让前台那个实例出声，后台实例自动静音**的小工具。
v1.3 起**只开一个实例也生效**（以前只对多开有效）。

## 它解决什么

开了两个（或更多）MuMu 实例，各自跑游戏/挂机。原生行为是两个实例同时出声、混在一起听不清。
本工具实时跟随窗口焦点：**焦点在哪个实例，那个实例有声；其余实例静音**。切到别的程序（浏览器、桌面）时，所有实例一起静音。

只开一个实例时，默认同样跟随：**焦点在它身上就有声，切走就静音**（可在托盘菜单关掉，关掉则单实例时始终保持有声）。

## 当前状态

- 2026-11-07：**v1.3.2** 已编译落盘并在本机真实环境验证通过（右键 exe → 属性 → 详细信息 → 文件版本 1.3.2.0）。
- 按用户要求改为「**只管开关，绝不碰音量**」：删掉全部音量写入逻辑（`SetVolume` 方法直接从代码里移除），
  只保留 `SetMute(true/false)`。以前静音期间你调过音量，切回实例时工具会把音量“还原”成它记下的旧值
  （所以看起来像“非要给我调成 100%”），现在这条路径整段不存在了。详见 `工作记录\2026-11-07-只做开关不碰音量.md`。
- 上一版 v1.3.1 的修复一起保留：**v1.3「实例在前台、音量 100%，但系统音量合成器里仍是静音」**——
  旧逻辑把「已经被静音」当成会话原状记住，之后每次都“还原”成静音，永久锁死（Windows 还会持久化每个应用的静音，
  新会话一起来就是哑的）。现在该出声就无条件清掉静音。
- 同步保留 **焦点防抖 `debounceMs`（默认 250ms）**：输入法候选框 / 气泡提示 / 通知这类一闪而过的窗口不再触发静音。
- 已验证（真实实例 PID 35128、真实桌面焦点）：静音时把音量设成 0.42 → 切回实例 `mute=False` 且**音量仍是 0.42**；
  切走 `mute=True` 音量仍 0.42；音量 1.00 时切回 `mute=False`、切走 `mute=True`，音量全程不动。
- 待用户在真实使用中长时间挂机观察稳定性。

## Session 0 问题（v1.2 单实例/全场景失效的根因）

v1.2 由 WMI 的 `CommandLineEventConsumer` 直接拉起 exe。WMI 消费者跑在 **Session 0（服务会话）**，那里**没有用户桌面、也没有用户音频设备**：

- `GetForegroundWindow()` 恒为 0 → 永远「无前台实例」→ 要么全静音要么空转
- 音频会话枚举为空 → 想静音也静音不了、想恢复也恢复不了
- 工具进程活着，日志可能一条动作都没有

**v1.3 的修法（计划任务桥）**：

1. 安装时建一个交互式计划任务 `MuMuAudioRouter`（`schtasks /sc once /st 00:00 /ru Administrator /it`，登录模式 Interactive only）
2. WMI 消费者不再直接跑 exe，而是执行 `schtasks.exe /run /tn MuMuAudioRouter`
3. 这样拉起的进程落在**用户桌面会话（Session 1）**，焦点和音频设备都正常

> 关键坑：`CommandLineEventConsumer` **没有 Arguments 属性**，参数必须全部写进 `CommandLineTemplate`，否则 `Set-WmiInstance` 报 `Not found`。

另外 exe 自己也会兜底：启动时若发现 `ProcessIdToSessionId(自身) == 0`，会 `schtasks /run` 重新拉起自己再退出。

## 原理（为什么不用逆向 MuMu）

实测（2026-11-02，两个实例在跑）：

| 进程 | 角色 |
| --- | --- |
| `MuMuNxMain.exe` | 多开器 / 管理器 |
| `MuMuNxService.exe` | 服务 |
| `MuMuNxSVC.exe` | VirtualBox hypervisor 服务 |
| `MuMuNxDevice.exe` × N | 每个实例一个进程，显示窗口 + 音频 |

关键结论：

1. **每个实例是独立的 `MuMuNxDevice.exe` 进程**，窗口和音频会话同属该 PID。
2. Windows Core Audio 里，每个实例是**独立的音频会话**（`IAudioSessionManager2` 枚举可见），可分别 `ISimpleAudioVolume.SetMute`。
3. `.nemu` 里是 `AudioAdapter driver="DirectSound" enabledOut="true"` —— 声音走 VirtualBox 的 DirectSound 后端，在实例进程内输出，不改配置、不需要重启 VM。

所以完全不需要逆向管理器、不需要注入插件、不改 MuMu 任何文件。工具只是外部监听焦点 + 调 Windows 音频接口，MuMu 升级也不受影响。

## 用法

**正常情况什么都不用做**：装了 WMI 订阅 + 计划任务后，只要你启动 MuMu（多开器或任一实例），工具会自动被系统拉起，托盘出现喇叭图标。

1. 开一个实例：焦点在它上面就有声，切到别处自动静音。
2. 开两个实例：点哪个哪个出声，另一个自动静音。
3. 关掉 MuMu（多开器 + 实例都关），工具等 6 秒确认后自动退出，并把所有实例恢复有声。
4. 右键托盘图标：
   - **启用/暂停**：临时停掉跟随（不动当前静音状态）
   - **无前台实例时全部静音**：默认开。关掉后，切到浏览器时上一个实例继续出声
   - **单实例时也跟随（只开一个实例也生效）**：默认开。关掉后，只开一个实例时始终保持有声、不受焦点影响
   - **随 MuMu 启动/关闭（安装/卸载）**：管理 WMI 订阅 + 计划任务，会弹 UAC 提权
   - **开机自启（备用）**：不走 WMI 时的兜底方案
   - **打开日志**
   - **退出**（退出时会把所有实例恢复有声）

设置改完即写入 `router.json`，下次启动沿用。

## 文件

```
MuMu实例音频路由\
├── MuMuAudioRouter.exe          编译好的成品（39KB，双击即用）
├── MuMuAudioRouter.exe.bak-v1.2 旧版回滚备份
├── src\MuMuAudioRouter.cs       源码（约 1180 行）
├── build.ps1                    重新编译
├── router.json                  配置（已生成一份默认值）
├── install-autostart.ps1        安装 WMI 订阅 + 计划任务桥
├── uninstall-autostart.ps1      卸载（含删除计划任务）
├── router.log                   运行日志
├── tools\diag_sessions.ps1      诊断：枚举前台窗口 + 所有音频会话（PID/状态/音量/静音）
├── tools\diag_tree.ps1          诊断：模拟进程树匹配，看实例归属
├── tools\force_foreground.ps1   诊断：AttachThreadInput 强制切前台（参数=目标PID）
├── tools\session_volume.ps1     诊断：按 PID 读/写某会话的音量与静音（复验“工具不碰音量”）
├── tools\verify_switch.ps1      焦点切换复验脚本
├── README.md
├── CHANGELOG.md
└── 工作记录\
```

## 重新编译

```powershell
powershell -ExecutionPolicy Bypass -File E:\MuMu实例音频路由\build.ps1
```

用系统自带的 .NET Framework 4.8 `csc.exe`，无需装 VS / .NET SDK，产物是单文件。

> 编译前先关掉正在跑的 `MuMuAudioRouter.exe`，否则 exe 被占用写不进去。

## 配置 router.json

```json
{
  "processNames": ["MuMuNxDevice"],
  "hostProcessNames": ["MuMuNxMain", "MuMuNxDevice"],
  "muteWhenNoInstanceFocused": true,
  "singleInstanceFollow": true,
  "exitWithMuMu": true,
  "exitDelaySeconds": 6,
  "pollMs": 100,
  "debounceMs": 250,
  "reconcileMs": 2000,
  "idlePollMs": 1000
}
```

- `processNames`：哪些进程算「实例」。老版 MuMu 可加 `MuMuPlayer`。
- `hostProcessNames`：判断 MuMu 是否还在跑用的进程名。全部消失才认为 MuMu 关闭。
- `muteWhenNoInstanceFocused`：没有实例在前台时是否全部静音。
- `singleInstanceFollow`：**单实例时是否也跟随焦点**（v1.3 新增，默认 true）。false = 只开一个实例时始终有声。
- `exitWithMuMu` / `exitDelaySeconds`：MuMu 关闭后多久自动退出。
- `pollMs`：前台窗口检测间隔。`debounceMs`：焦点要稳定多少毫秒才动作（默认 250，防输入法候选框/气泡/通知误伤；设 0 = 关掉防抖）。
  `reconcileMs`：全量会话重建间隔（兜底新会话/新实例，也是「被别的程序静音后自动放开」的自愈节奏）。
- `idlePollMs`：没有实例进程时的空转间隔（省 CPU）。

## 占用

- 空闲时每 30 秒调 `EmptyWorkingSet`，工作集可压到 1MB 以内
- 私有内存约 22–30MB（.NET WinForms 基线），CPU 空闲 ≈ 0
- **不跑 MuMu 时工具根本不存在**（WMI 触发式启动，平时零常驻）

## 已知限制

- **音量不归本工具管**（v1.3.2 起）：它只写静音开关，不写音量。你在系统音量合成器里设多少就是多少，
  工具不会覆盖；反过来，如果 MuMu / Windows 自己把会话音量重置了，工具也不会去纠正。
- 只处理默认播放设备（扬声器）。多声卡同时输出的情况未覆盖。
- **手动静音会被自动放开**：因为 v1.3.1 起「该出声就不允许静音」，如果你自己在音量合成器里把 MuMu 静音，
  工具会在下次对账（≤2 秒）把它重新打开。要静音请用托盘「启用跟随」的暂停，或者直接关工具。
- 若某实例的音频是由**子进程**播放，工具已按进程树把子进程算进该实例；但若 MuMu 改成跨进程共享音频服务（目前不是），需要重新适配。
- 静音是对整个进程的音频会话生效：同一个实例里「游戏声 + 其它提示音」会一起静音，这是需求本身要的效果。
- 计划任务是「Interactive only」，**必须登录桌面后才生效**；无人登录时工具不会被拉起（这时也没有声音需要路由）。
