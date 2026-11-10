# CHANGELOG

## v1.3.2 — 2026-11-07

按用户要求改成「**只管开关，不碰音量**」。

- 删掉全部音量写入能力：`SimpleVolume.SetVolume()` 直接移除（类里只剩 `SetMute`），`AudioSession.Volume`
  / `GetMasterVolume` / `SavedState.Volume` 一并删除。编译期就杜绝再写音量。
- 根因：v1.3.1 的 `MuteAndRemember` 会在静音时记住当时音量，`EnsureSound` / `RestoreIfSaved` 再“还原”。
  于是「静音期间你调了音量 → 切回实例，工具把音量改回它记下的旧值」，观感就是“他非要调成 100%”。
- `EnsureSound()` 只剩「有静音就清掉」+ 清记录；`RestoreIfSaved()` 只还原静音。
- 新增诊断工具 `tools/session_volume.ps1`（按 PID 读/写会话音量与静音）。注意参数是 `-ProcessId`，
  且文件必须 UTF-8 **带 BOM**，否则 `powershell -File` 按 ANSI 解析中文注释会报 `Unexpected token ')'`。
- 版本号 1.3.2.0。实测（真实实例 PID 35128）：静音时把音量设 0.42 → 切回 `mute=False` 且 `vol=0.42`（旧版会回 1.00）；
  切走 `mute=True`、`vol=0.42`；音量 1.00 时切回 `mute=False`、切走 `mute=True`，音量全程不动。

## v1.3.1 — 2026-11-07

修「实例就在前台、音量 100%，但音量合成器里还是静音」。

- **根因（自锁）**：`MuteAndRemember` 把「当前已静音」当成会话原始状态存进 `_saved`，
  该出声时 `RestoreIfSaved` 又照着「原状」还原成静音。只要会话在工具第一次看到它之前就是静音，
  就永久锁死在静音，且日志还会写「出声」（因为没真动作，不带 `| 恢复:` 后缀）。
- **为什么必中招**：Windows 会把每个应用的静音状态持久化到
  `HKCU\...\LowRegistry\Audio\PolicyConfig\PropertyStore`，上一轮被本工具静音过的应用，
  新会话起来就是静音。
- 新增 `EnsureSound()`：该出声就**无条件清掉静音**（只还原本工具改过的音量），不再「还原静音」。
- `MassRestore()`（暂停 / 退出 / 全部恢复）也强制放开实例会话静音，不再依赖 `_saved`。
- 新增 `debounceMs`（默认 250）：焦点要稳定才动作，输入法候选框/气泡/通知这类瞬态窗口不再把正在玩的实例静音。
- 版本号 1.3.1.0。实测：前台→`mute=False`、切走→`mute=True`、切回→`mute=False`；
  外部强行静音后 2 秒内自动放开。

## v1.3 — 2026-11-04

修「只开一个实例就失效」+ 从根上修「WMI 拉起后全程零动作」。

- **根因**：WMI `CommandLineEventConsumer` 把工具拉在 **Session 0（服务会话）**，那里没有用户桌面（`GetForegroundWindow` 恒 0）也没有用户音频设备（会话枚举为空），工具活着但完全空转。v1.2 能工作是因为当时手动双击（Session 1）。
- 新增：**计划任务桥**——安装时创建交互式计划任务 `MuMuAudioRouter`（`/it` + Interactive only），WMI 消费者改为 `schtasks.exe /run /tn MuMuAudioRouter`，保证拉起在 Session 1。
- 新增：exe 启动自检 `SessionId==0` 时自动 `schtasks /run` 重新拉起自己并退出（兜底）。
- 新增：`singleInstanceFollow` 配置 + 托盘菜单「单实例时也跟随（只开一个实例也生效）」，默认开；关掉后只开一个实例时始终保持有声。
- 新增：启动日志打印 `Session=N`，`Apply` 的两处静默 return 改为限频留痕（30 秒一次），不再"日志一片空白"。
- 修复：安装脚本的 WMI 消费者参数写法——`CommandLineEventConsumer` 没有 `Arguments` 属性，必须全部塞进 `CommandLineTemplate`。
- 卸载脚本同步删除计划任务。
- 版本号 1.3.0.0。

## v1.2 — 2026-11-02

- 源码重写（原因见下）：一次 PowerShell 误操作把源码里的中文全部写成 mojibake，编译失败。按原骨架逐类重写，逻辑不变、中文修正。
- exe 内嵌版本号（右键属性 → 详细信息 → 文件版本 1.2.0.0）。
- 空闲时额外 `GC.Collect()`，抑制 .NET 托管堆随运行时长缓慢增长。
- 菜单精简：去掉无效的对话框残留项。

## v1.1 — 2026-11-02

生命周期与占用优化。

- 新增：**随 MuMu 启动**（Windows 永久 WMI 事件订阅，监听 `Win32_ProcessStartTrace`，MuMu 进程创建时自动拉起工具，平时零常驻）。
- 新增：**随 MuMu 关闭退出**（主体进程 `MuMuNxMain` + `MuMuNxDevice` 全部消失 N 秒后，恢复全部声音并自行退出）。
- 新增：60 秒宽限——从没检测到 MuMu 时不立刻退出，避免手动双击后自己消失。
- 新增：空闲时压缩工作集（`EmptyWorkingSet`），无实例在前台时每 30 秒回收一次。
- 新增：启动清理——取消上次异常退出残留的静音。
- 新增：托盘菜单增加「随 MuMu 启动/关闭（安装/卸载）」「开机自启（备用）」。
- 修复：图标句柄泄漏（`GetHicon` 后补 `DestroyIcon`，三种状态图标改为缓存）。
- 修复：COM 会话对象用后显式释放。

## v1.0 — 2026-11-02

首个可用版本。

- 基于 Windows Core Audio 会话，按实例进程独立静音。
- 前台窗口 → 顶层窗口 → PID 判定当前实例，其余实例 `SetMute(true)`。
- 记住并还原原音量/静音状态；无前台实例时全部静音（可关）。
- 100ms 前台轮询 + 2s 会话对账，兜底实例重启后的会话重建。
- 托盘图标（绿=有前台实例、橙=全部静音、灰=暂停）、右键菜单、日志。
