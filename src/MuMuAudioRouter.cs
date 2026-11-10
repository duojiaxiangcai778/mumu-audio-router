// MuMu 实例音频路由 v1.3.2
// 作用：多开 MuMu Player 12 时，只让前台实例出声，其余实例自动静音。
// 原理：Windows Core Audio 按进程会话静音；窗口 PID == 音频会话 PID，无需逆向 MuMu。
// 生命周期：随 MuMu 启动（WMI 事件订阅 → 计划任务桥 → 用户交互会话拉起），MuMu 全部退出后自行退出。
// v1.3：修复 WMI 把进程拉进 Session 0 导致全程空转（看不到桌面和音频设备）；
//       新增单实例跟随开关；所有静默返回路径补日志。
// v1.3.1：修「实例在前台、音量 100%，但系统音量合成器里仍是静音」——
//       旧逻辑把「已经被静音」当成该会话的原始状态记下来，之后每次该出声时都“还原”成静音，
//       于是永久锁死；而 Windows 会把每个应用的静音状态持久化，新会话一起来就是静音，所以必中招。
//       现在：该出声就无条件清掉静音。
// v1.3.2：用户要求「只管开关、不许碰音量」——彻底删掉音量写入逻辑（SetVolume / 音量记忆还原），
//       本工具只做 SetMute(true/false)。音量由用户自己在系统音量合成器里定（已手动设成 100%）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("MuMuAudioRouter")]
[assembly: System.Reflection.AssemblyDescription("MuMu 实例音频路由 - 前台实例出声，后台实例静音")]
[assembly: System.Reflection.AssemblyProduct("MuMuAudioRouter")]
[assembly: System.Reflection.AssemblyCompany("local")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.2.0")]
[assembly: System.Reflection.AssemblyVersion("1.3.2.0")]

namespace MuMuAudioRouter
{
    // ===================== 配置 =====================
    internal sealed class Config
    {
        public string[] ProcessNames = new[] { "MuMuNxDevice" };
        // MuMu 的「主体」进程：这些全都消失才认为 MuMu 已经关了，工具随之退出
        public string[] HostProcessNames = new[] { "MuMuNxMain", "MuMuNxDevice" };
        public bool MuteWhenNoInstanceFocused = true;
        // 单实例（只开一个 MuMu）时是否也按焦点跟随。
        // true（默认）= 单开一个也生效：MuMu 在前台出声，切到别的程序静音；
        // false = 只有一个实例时不干预，始终保持有声。
        public bool SingleInstanceFollow = true;
        public bool ExitWithMuMu = true;
        public int ExitDelaySeconds = 6;
        public int PollMs = 100;
        // 焦点稳定多久才应用（毫秒）。防止输入法候选框、气泡提示、通知这类一闪而过的窗口
        // 在 100ms 轮询里被当成「切走了」而把正在玩的实例静音掉。
        public int DebounceMs = 250;
        public int ReconcileMs = 2000;
        public int IdlePollMs = 1000;

        public static Config Load(string path)
        {
            var c = new Config();
            try
            {
                if (!File.Exists(path)) return c;
                string json = File.ReadAllText(path, Encoding.UTF8);

                var arr = ReadStringArray(json, "processNames");
                if (arr != null && arr.Length > 0) c.ProcessNames = arr;

                var hosts = ReadStringArray(json, "hostProcessNames");
                if (hosts != null && hosts.Length > 0) c.HostProcessNames = hosts;

                bool b; int i;
                if (TryBool(json, "muteWhenNoInstanceFocused", out b)) c.MuteWhenNoInstanceFocused = b;
                if (TryBool(json, "singleInstanceFollow", out b)) c.SingleInstanceFollow = b;
                if (TryBool(json, "exitWithMuMu", out b)) c.ExitWithMuMu = b;
                if (TryInt(json, "exitDelaySeconds", out i) && i >= 2 && i <= 600) c.ExitDelaySeconds = i;
                if (TryInt(json, "pollMs", out i) && i >= 20 && i <= 2000) c.PollMs = i;
                if (TryInt(json, "debounceMs", out i) && i >= 0 && i <= 3000) c.DebounceMs = i;
                if (TryInt(json, "reconcileMs", out i) && i >= 300 && i <= 60000) c.ReconcileMs = i;
                if (TryInt(json, "idlePollMs", out i) && i >= 200 && i <= 5000) c.IdlePollMs = i;
            }
            catch { }
            return c;
        }

        static string[] ReadStringArray(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return null;
            var list = new List<string>();
            foreach (Match n in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
            {
                string v = n.Groups[1].Value.Trim();
                if (v.Length > 0) list.Add(v);
            }
            return list.ToArray();
        }

        static bool TryBool(string json, string key, out bool v)
        {
            v = false;
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            v = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        static bool TryInt(string json, string key, out int v)
        {
            v = 0;
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, out v);
        }

        static string ToJsonArray(string[] arr)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"").Append(arr[i].Replace("\"", "\\\"")).Append("\"");
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>把当前配置写回 router.json（改设置后持久化）</summary>
        public void Save(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"processNames\": " + ToJsonArray(ProcessNames) + ",");
                sb.AppendLine("  \"hostProcessNames\": " + ToJsonArray(HostProcessNames) + ",");
                sb.AppendLine("  \"muteWhenNoInstanceFocused\": " + (MuteWhenNoInstanceFocused ? "true" : "false") + ",");
                sb.AppendLine("  \"singleInstanceFollow\": " + (SingleInstanceFollow ? "true" : "false") + ",");
                sb.AppendLine("  \"exitWithMuMu\": " + (ExitWithMuMu ? "true" : "false") + ",");
                sb.AppendLine("  \"exitDelaySeconds\": " + ExitDelaySeconds + ",");
                sb.AppendLine("  \"pollMs\": " + PollMs + ",");
                sb.AppendLine("  \"debounceMs\": " + DebounceMs + ",");
                sb.AppendLine("  \"reconcileMs\": " + ReconcileMs + ",");
                sb.AppendLine("  \"idlePollMs\": " + IdlePollMs);
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("保存配置失败: " + ex.Message); }
        }
    }

    // ===================== 日志 =====================
    internal static class Log
    {
        static readonly object Gate = new object();
        static string _path;

        public static string Path { get { return _path; } }

        public static void Init(string dir)
        {
            _path = System.IO.Path.Combine(dir, "router.log");
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    string txt = File.ReadAllText(_path, Encoding.UTF8);
                    File.WriteAllText(_path, txt.Substring(txt.Length / 2), Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ===================== Win32 =====================
    internal static class Win
    {
        public const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll")]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        /// <summary>把工作集还给系统，任务管理器里内存会明显下降</summary>
        public static void TrimWorkingSet()
        {
            try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
        }

        /// <summary>一次快照取全部进程：pid -> (父pid, 进程名无扩展名)</summary>
        public static void Snapshot(out Dictionary<int, int> parentOf, out Dictionary<int, string> nameOf)
        {
            parentOf = new Dictionary<int, int>();
            nameOf = new Dictionary<int, string>();

            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == new IntPtr(-1)) return;
            try
            {
                var pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        int pid = (int)pe.th32ProcessID;
                        parentOf[pid] = (int)pe.th32ParentProcessID;
                        string exe = (pe.szExeFile ?? "").Trim();
                        int dot = exe.LastIndexOf('.');
                        nameOf[pid] = dot > 0 ? exe.Substring(0, dot) : exe;
                    }
                    while (Process32Next(snap, ref pe));
                }
            }
            finally { CloseHandle(snap); }
        }
    }

    // ===================== 进程树 =====================
    internal sealed class InstanceMap
    {
        public readonly Dictionary<int, HashSet<int>> Instances = new Dictionary<int, HashSet<int>>();
        public readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        public static InstanceMap Build(string[] matchNames)
        {
            var map = new InstanceMap();
            Dictionary<int, int> parentOf;
            Dictionary<int, string> nameOf;
            Win.Snapshot(out parentOf, out nameOf);

            var children = new Dictionary<int, List<int>>();
            foreach (var kv in parentOf)
            {
                List<int> list;
                if (!children.TryGetValue(kv.Value, out list))
                {
                    list = new List<int>();
                    children[kv.Value] = list;
                }
                list.Add(kv.Key);
            }

            foreach (var kv in nameOf)
            {
                if (!Matches(kv.Value, matchNames)) continue;
                int root = kv.Key;
                var set = new HashSet<int> { root };
                var q = new Queue<int>();
                q.Enqueue(root);
                int guard = 0;
                while (q.Count > 0 && guard++ < 100000)
                {
                    int cur = q.Dequeue();
                    List<int> kids;
                    if (!children.TryGetValue(cur, out kids)) continue;
                    foreach (int k in kids) { if (set.Add(k)) q.Enqueue(k); }
                }
                map.Instances[root] = set;
                map.Names[root] = nameOf[root];
            }
            return map;
        }

        static bool Matches(string procName, string[] patterns)
        {
            foreach (var p in patterns)
            {
                if (!string.IsNullOrEmpty(p) &&
                    string.Equals(procName, p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public int OwnerOf(int pid)
        {
            foreach (var kv in Instances)
            {
                if (kv.Value.Contains(pid)) return kv.Key;
            }
            return -1;
        }

        public static bool AnyHostProcessRunning(string[] hostNames)
        {
            Dictionary<int, int> parentOf;
            Dictionary<int, string> nameOf;
            Win.Snapshot(out parentOf, out nameOf);
            foreach (var kv in nameOf)
            {
                foreach (var h in hostNames)
                {
                    if (string.Equals(kv.Value, h, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }

    // ===================== Core Audio =====================
    internal sealed class AudioSession
    {
        public string Key;
        public int Pid;
        public int State;
        public bool Mute;
        internal object Control;
        internal AudioApi.SimpleVolume Vol;
    }

    internal static class AudioApi
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            int GetCount(out int count);
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, int flags, out IntPtr sessionCtl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, int flags, out IntPtr audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            int GetCount(out int count);
            int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(IntPtr name, IntPtr eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath(IntPtr path, IntPtr eventContext);
            int GetGroupingParam(out Guid param);
            int SetGroupingParam(ref Guid param, IntPtr eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(IntPtr name, IntPtr eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath(IntPtr path, IntPtr eventContext);
            int GetGroupingParam(out Guid param);
            int SetGroupingParam(ref Guid param, IntPtr eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
            int GetSessionIdentifier(out IntPtr retVal);
            int GetSessionInstanceIdentifier(out IntPtr retVal);
            int GetProcessId(out int pid);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, IntPtr eventContext);
            int GetMasterVolume(out float level);
            int SetMute(bool mute, IntPtr eventContext);
            int GetMute(out bool mute);
        }

        public const int AUDIO_FLOW_RENDER = 0;
        public const int DEVICE_STATE_ACTIVE = 1;

        /// <summary>
        /// 只暴露静音控制。v1.3.2 起**故意不提供 SetVolume**：
        /// 本工具只负责开关，音量一律交回用户在系统音量合成器里设定，避免任何“被改成 100%”的观感。
        /// </summary>
        public sealed class SimpleVolume
        {
            readonly ISimpleAudioVolume _v;
            public SimpleVolume(ISimpleAudioVolume v) { _v = v; }
            public void SetMute(bool mute) { try { _v.SetMute(mute, IntPtr.Zero); } catch { } }
        }

        /// <summary>枚举所有渲染设备上的全部音频会话（返回后请调用 Release 释放）</summary>
        public static List<AudioSession> Enumerate()
        {
            var list = new List<AudioSession>();
            try
            {
                var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDeviceCollection coll;
                if (en.EnumAudioEndpoints(AUDIO_FLOW_RENDER, DEVICE_STATE_ACTIVE, out coll) != 0) return list;
                int devCount;
                if (coll.GetCount(out devCount) != 0) return list;

                for (int d = 0; d < devCount; d++)
                {
                    IMMDevice dev;
                    if (coll.Item(d, out dev) != 0) continue;
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    object mgrObj;
                    if (dev.Activate(ref iid, 0, IntPtr.Zero, out mgrObj) != 0) continue;
                    var mgr = (IAudioSessionManager2)mgrObj;
                    IAudioSessionEnumerator ses;
                    if (mgr.GetSessionEnumerator(out ses) != 0) continue;
                    int n;
                    if (ses.GetCount(out n) != 0) continue;

                    for (int i = 0; i < n; i++)
                    {
                        IAudioSessionControl ctl;
                        if (ses.GetSession(i, out ctl) != 0) continue;
                        var s = new AudioSession();
                        s.Control = ctl;
                        try
                        {
                            var c2 = (IAudioSessionControl2)ctl;
                            int pid;
                            if (c2.GetProcessId(out pid) == 0) s.Pid = pid;

                            int st;
                            if (c2.GetState(out st) == 0) s.State = st;

                            IntPtr keyPtr;
                            if (c2.GetSessionInstanceIdentifier(out keyPtr) == 0 && keyPtr != IntPtr.Zero)
                            {
                                s.Key = Marshal.PtrToStringUni(keyPtr);
                                Marshal.FreeCoTaskMem(keyPtr);
                            }
                        }
                        catch { }

                        try
                        {
                            var vol = (ISimpleAudioVolume)ctl;
                            bool m;
                            if (vol.GetMute(out m) == 0) s.Mute = m;
                            s.Vol = new SimpleVolume(vol);
                        }
                        catch { }

                        if (s.Key == null) s.Key = "pid:" + s.Pid + ":idx:" + i;
                        list.Add(s);
                    }
                }
            }
            catch (Exception ex) { Log.Write("枚举音频会话失败: " + ex.Message); }
            return list;
        }

        public static void Release(List<AudioSession> list)
        {
            foreach (var s in list)
            {
                try { if (s.Control != null) Marshal.ReleaseComObject(s.Control); }
                catch { }
            }
        }
    }

    // ===================== 主上下文 =====================
    internal sealed class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MuMuAudioRouter";

        readonly Config _cfg;
        readonly string _exeDir;
        readonly NotifyIcon _tray;
        readonly ContextMenuStrip _menu;

        System.Windows.Forms.Timer _fgTimer;
        System.Windows.Forms.Timer _maintenanceTimer;

        InstanceMap _map = new InstanceMap();
        DateTime _mapStamp = DateTime.MinValue;
        IntPtr _lastFg = IntPtr.Zero;
        IntPtr _pendingFg = IntPtr.Zero;
        DateTime _pendingSince = DateTime.MinValue;
        int _activeRoot = -1;
        string _activeTitle = "";
        bool _enabled = true;
        long _applyCount;
        bool _singleBypass;                      // 单实例旁路：只有一个实例且不跟随时，保持全部有声
        DateTime _lastQuietLog = DateTime.MinValue;
        DateTime _noMuMuSince = DateTime.MinValue;
        bool _everSawMuMu;
        readonly DateTime _startUtc = DateTime.UtcNow;
        DateTime _lastTrim = DateTime.MinValue;
        bool _exiting;

        Icon _iconOn, _iconIdle, _iconOff;

        /// <summary>只记静音原状（v1.3.2 起不再记音量，工具不碰音量）</summary>
        sealed class SavedState
        {
            public bool Mute;
        }

        readonly Dictionary<string, SavedState> _saved = new Dictionary<string, SavedState>();

        internal static int SafeSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return -1; }
        }

        // 静默路径也留痕（限频 30 秒一次），避免“工具在跑但日志一条都没有”这种黑洞
        void QuietLog(string msg)
        {
            if ((DateTime.UtcNow - _lastQuietLog).TotalSeconds < 30) return;
            _lastQuietLog = DateTime.UtcNow;
            Log.Write(msg);
        }

        public TrayContext()
        {
            _exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _cfg = Config.Load(Path.Combine(_exeDir, "router.json"));
            Log.Init(_exeDir);
            ScriptWriter.EnsureScripts(_exeDir);
            Log.Write("=== 启动 v1.3.2 === 实例进程: " + string.Join(",", _cfg.ProcessNames)
                      + " | 主体进程: " + string.Join(",", _cfg.HostProcessNames)
                      + " | 无前台全静音=" + _cfg.MuteWhenNoInstanceFocused
                      + " | 单实例跟随=" + _cfg.SingleInstanceFollow
                      + " | 随MuMu退出=" + _cfg.ExitWithMuMu
                      + " | Session=" + SafeSessionId());

            // 上次异常退出可能残留静音 → 启动先把实例会话全部放开，再按当前焦点施加
            ResetAllInstanceMute();

            _menu = new ContextMenuStrip();
            _tray = new NotifyIcon
            {
                Visible = true,
                ContextMenuStrip = _menu,
                Text = "MuMu 实例音频路由"
            };
            _menu.Opening += (s, e) => BuildMenu();
            _tray.DoubleClick += (s, e) => ToggleEnabled();
            _tray.MouseUp += TrayMouseUp;
            UpdateIcon();

            _fgTimer = new System.Windows.Forms.Timer { Interval = Math.Max(30, _cfg.PollMs) };
            _fgTimer.Tick += (s, e) => OnForegroundTick();
            _fgTimer.Start();

            _maintenanceTimer = new System.Windows.Forms.Timer { Interval = Math.Max(500, _cfg.ReconcileMs) };
            _maintenanceTimer.Tick += (s, e) => OnMaintenance();
            _maintenanceTimer.Start();
        }

        void TrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(_tray, null);
            }
        }

        // ---------- 菜单 ----------
        void BuildMenu()
        {
            _menu.Items.Clear();

            string cur = _activeRoot >= 0
                ? "当前实例：" + (_activeTitle.Length > 0 ? "「" + _activeTitle + "」" : "PID " + _activeRoot) + " 出声"
                : (_cfg.MuteWhenNoInstanceFocused ? "当前实例：无（全部静音）" : "当前实例：无（保持有声）");
            _menu.Items.Add(new ToolStripMenuItem(cur) { Enabled = false });
            _menu.Items.Add(new ToolStripMenuItem((_enabled ? "状态：跟随中" : "状态：已暂停") + "  ·  已处理 " + _applyCount + " 次")
                { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(new ToolStripMenuItem("启用跟随", null, (s, e) => ToggleEnabled()) { Checked = _enabled });

            _menu.Items.Add(new ToolStripMenuItem("无前台实例时全部静音", null, (s, e) =>
            {
                _cfg.MuteWhenNoInstanceFocused = !_cfg.MuteWhenNoInstanceFocused;
                Log.Write("设置：无前台全静音 = " + _cfg.MuteWhenNoInstanceFocused);
                _cfg.Save(Path.Combine(_exeDir, "router.json"));
                Apply(_activeRoot, "改设置");
            }) { Checked = _cfg.MuteWhenNoInstanceFocused });

            _menu.Items.Add(new ToolStripMenuItem("单实例时也跟随（只开一个实例也生效）", null, (s, e) =>
            {
                _cfg.SingleInstanceFollow = !_cfg.SingleInstanceFollow;
                Log.Write("设置：单实例跟随 = " + _cfg.SingleInstanceFollow);
                _cfg.Save(Path.Combine(_exeDir, "router.json"));
                _mapStamp = DateTime.MinValue;
                Apply(_activeRoot, "改设置");
            }) { Checked = _cfg.SingleInstanceFollow });

            _menu.Items.Add(new ToolStripMenuItem("立即重扫实例与会话", null, (s, e) =>
            {
                _mapStamp = DateTime.MinValue;
                Apply(_activeRoot, "手动重扫");
            }));

            _menu.Items.Add(new ToolStripMenuItem("全部恢复有声", null, (s, e) =>
            {
                MassRestore();
                _enabled = false;
                UpdateIcon();
            }));

            _menu.Items.Add(new ToolStripSeparator());

            // 随 MuMu 自动启停
            bool wmi = WmiInstaller.IsInstalled();
            _menu.Items.Add(new ToolStripMenuItem(
                wmi ? "✓ 随 MuMu 启动 / 关闭（已安装）" : "随 MuMu 启动 / 关闭（点此安装）",
                null, (s, e) => ToggleWmi()) { Checked = wmi });

            _menu.Items.Add(new ToolStripMenuItem("开机自启（备用方案）", null, (s, e) => SetAutoStart(!IsAutoStart()))
                { Checked = IsAutoStart() });

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("打开日志", null, (s, e) =>
            {
                try { Process.Start("notepad.exe", Log.Path); } catch { }
            }));
            _menu.Items.Add(new ToolStripMenuItem("退出（会恢复全部实例）", null, (s, e) => ExitApp()));
        }

        void ToggleEnabled()
        {
            if (_exiting) return;
            _enabled = !_enabled;
            Log.Write("跟随 = " + (_enabled ? "启用" : "暂停"));
            if (_enabled) Apply(_activeRoot, "恢复跟随");
            else MassRestore();
            UpdateIcon();
        }

        void ToggleWmi()
        {
            try
            {
                if (WmiInstaller.IsInstalled())
                {
                    if (MessageBox.Show("要取消「随 MuMu 自动启动」吗？\n\n取消后需要手动双击 MuMuAudioRouter.exe。",
                            "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    WmiInstaller.RunScript("uninstall-autostart.ps1");
                }
                else
                {
                    if (MessageBox.Show("会在系统里注册一个事件订阅：MuMu 启动时自动拉起本工具（需要一次管理员确认）。\n\n继续？",
                            "安装随 MuMu 启动", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    WmiInstaller.RunScript("install-autostart.ps1");
                }
            }
            catch (Exception ex)
            {
                Log.Write("WMI 切换失败: " + ex.Message);
                MessageBox.Show("操作失败：" + ex.Message + "\n\n详情见日志。", "出错了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            Log.Write("退出：恢复全部实例");
            MassRestore();
            try { _fgTimer.Stop(); _maintenanceTimer.Stop(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { DisposeIcons(); } catch { }
            ExitThread();
        }

        // ---------- 图标 ----------
        void UpdateIcon()
        {
            try
            {
                if (_iconOn == null) BuildIcons();
                _tray.Icon = !_enabled ? _iconOff : (_activeRoot >= 0 ? _iconOn : _iconIdle);
                _tray.Text = "MuMu 实例音频路由 — " +
                             (!_enabled ? "已暂停"
                              : (_activeRoot >= 0 ? "跟随中（" + (_activeTitle.Length > 0 ? _activeTitle : "有前台实例") + " 出声）"
                                                  : "跟随中（无前台实例）"));
            }
            catch { }
        }

        void BuildIcons()
        {
            _iconOn = MakeIcon(true, true);
            _iconIdle = MakeIcon(true, false);
            _iconOff = MakeIcon(false, false);
        }

        void DisposeIcons()
        {
            foreach (var i in new[] { _iconOn, _iconIdle, _iconOff })
            {
                if (i != null) i.Dispose();
            }
            _iconOn = _iconIdle = _iconOff = null;
        }

        static Icon MakeIcon(bool enabled, bool hasActive)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color main = !enabled ? Color.FromArgb(150, 150, 150)
                          : hasActive ? Color.FromArgb(64, 195, 110)
                          : Color.FromArgb(230, 170, 60);

                using (var brush = new SolidBrush(main))
                {
                    var body = new[]
                    {
                        new PointF(5, 12), new PointF(11, 12), new PointF(18, 5),
                        new PointF(18, 27), new PointF(11, 20), new PointF(5, 20)
                    };
                    g.FillPolygon(brush, body);
                }

                using (var pen = new Pen(main, 2.4f))
                {
                    g.DrawArc(pen, 15f, 10.5f, 9f, 11f, -60f, 120f);
                    if (hasActive) g.DrawArc(pen, 15f, 6.5f, 15f, 19f, -55f, 110f);
                }

                if (!enabled)
                {
                    using (var pen = new Pen(Color.FromArgb(205, 70, 70), 3f))
                        g.DrawLine(pen, 4, 28, 28, 4);
                }

                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally { Win.DestroyIcon(h); }
            }
        }

        // ---------- 前台检测 ----------
        void OnForegroundTick()
        {
            if (_exiting || !_enabled) return;

            IntPtr fg = Win.GetForegroundWindow();

            // 已经应用过这个前台窗口
            if (fg == _lastFg) { _pendingFg = IntPtr.Zero; return; }

            // 焦点防抖：新窗口要稳定 DebounceMs 才动作，
            // 免得输入法候选框 / 气泡提示 / 通知一闪而过就把正在玩的实例静音
            if (fg != _pendingFg)
            {
                _pendingFg = fg;
                _pendingSince = DateTime.UtcNow;
                return;
            }
            if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < _cfg.DebounceMs) return;

            _lastFg = fg;
            _pendingFg = IntPtr.Zero;
            int root = ResolveInstance(fg);
            Apply(root, "焦点变化");
        }

        int ResolveInstance(IntPtr fgHwnd)
        {
            if (fgHwnd == IntPtr.Zero) return -1;
            IntPtr root = Win.GetAncestor(fgHwnd, Win.GA_ROOT);
            if (root == IntPtr.Zero) root = fgHwnd;
            if (!Win.IsWindowVisible(root)) return -1;

            int pid;
            Win.GetWindowThreadProcessId(root, out pid);
            if (pid == 0) return -1;

            RefreshMap();
            int owner = _map.OwnerOf(pid);
            if (owner >= 0) _activeTitle = WindowTitle(root);
            return owner;
        }

        static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                Win.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        void RefreshMap()
        {
            if ((DateTime.UtcNow - _mapStamp).TotalMilliseconds < 1500) return;
            _map = InstanceMap.Build(_cfg.ProcessNames);
            _mapStamp = DateTime.UtcNow;
        }

        // ---------- 周期维护：对账 + 生命周期 ----------
        void OnMaintenance()
        {
            if (_exiting) return;

            // 1) 生命周期：MuMu 全没了就自己退
            if (_cfg.ExitWithMuMu)
            {
                bool alive = InstanceMap.AnyHostProcessRunning(_cfg.HostProcessNames);
                if (alive)
                {
                    _noMuMuSince = DateTime.MinValue;
                    _everSawMuMu = true;
                }
                else
                {
                    // 从没启动过 MuMu 的场景（手动双击启动、WMI 拉起但 MuMu 秒退）
                    // 给 60 秒宽限，避免刚双击就自己消失
                    if (!_everSawMuMu)
                    {
                        if ((DateTime.UtcNow - _startUtc).TotalSeconds > 60)
                        {
                            Log.Write("60 秒内未检测到 MuMu，工具退出");
                            ExitApp();
                        }
                        return;
                    }

                    if (_noMuMuSince == DateTime.MinValue)
                    {
                        _noMuMuSince = DateTime.UtcNow;
                        Log.Write("MuMu 主体进程已全部退出，等待 " + _cfg.ExitDelaySeconds + " 秒后自动退出");
                    }
                    else if ((DateTime.UtcNow - _noMuMuSince).TotalSeconds >= _cfg.ExitDelaySeconds)
                    {
                        Log.Write("MuMu 已关闭，工具自行退出");
                        ExitApp();
                        return;
                    }
                }
            }

            // 2) 对账：会话可能因实例重启而重建，需要重新施加静音
            if (_enabled) Apply(_activeRoot, "周期对账");

            // 3) 低占用：没有实例在前台（没人玩）时，回收托管堆 + 把工作集还给系统
            if ((DateTime.UtcNow - _lastTrim).TotalSeconds >= 30)
            {
                _lastTrim = DateTime.UtcNow;
                if (_activeRoot < 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Win.TrimWorkingSet();
                }
            }
        }

        // ---------- 核心：应用状态 ----------
        void Apply(int targetRoot, string reason)
        {
            if (_exiting || !_enabled) return;

            var all = new List<AudioSession>();
            try
            {
                RefreshMap();

                int instCount = _map.Instances.Count;
                if (instCount == 0)
                {
                    QuietLog("[" + reason + "] 未检测到实例进程，跳过");
                    if (targetRoot != -1) { _activeRoot = -1; UpdateIcon(); }
                    return;
                }

                if (targetRoot >= 0 && !_map.Instances.ContainsKey(targetRoot)) targetRoot = -1;

                // 单实例旁路：只有 1 个实例且「单实例跟随」关闭时，不静音、全部保持有声
                bool bypass = instCount == 1 && !_cfg.SingleInstanceFollow;
                bool changed = targetRoot != _activeRoot || bypass != _singleBypass;
                _activeRoot = targetRoot;
                _singleBypass = bypass;

                all = AudioApi.Enumerate();
                if (all.Count == 0)
                {
                    QuietLog("[" + reason + "] 未枚举到音频会话，跳过（设备无活动会话）");
                    return;
                }

                var liveKeys = new HashSet<string>();
                var muted = new List<int>();
                var restored = new List<int>();

                foreach (var s in all)
                {
                    liveKeys.Add(s.Key);
                    int owner = _map.OwnerOf(s.Pid);

                    if (owner < 0)
                    {
                        RestoreIfSaved(s);   // 非实例会话若被改过（PID 复用），还原
                        continue;
                    }

                    bool shouldSound;
                    if (bypass) shouldSound = true;
                    else if (targetRoot < 0) shouldSound = !_cfg.MuteWhenNoInstanceFocused;
                    else shouldSound = (owner == targetRoot);

                    if (shouldSound)
                    {
                        if (EnsureSound(s)) restored.Add(s.Pid);
                    }
                    else
                    {
                        if (MuteAndRemember(s)) muted.Add(s.Pid);
                    }
                }

                PruneSaved(liveKeys);
                _applyCount++;

                if (changed || muted.Count > 0 || restored.Count > 0)
                {
                    string desc;
                    if (bypass)
                        desc = "只有 1 个实例且单实例跟随已关闭 → 保持有声";
                    else if (targetRoot >= 0)
                        desc = "实例 PID=" + targetRoot + (_activeTitle.Length > 0 ? "「" + _activeTitle + "」" : "") + " 出声";
                    else
                        desc = _cfg.MuteWhenNoInstanceFocused ? "无前台实例 → 全部静音" : "无前台实例 → 保持有声";
                    if (muted.Count > 0) desc += " | 静音: " + string.Join(",", muted.ToArray());
                    if (restored.Count > 0) desc += " | 恢复: " + string.Join(",", restored.ToArray());
                    Log.Write("[" + reason + "] " + desc);
                }

                UpdateIcon();
            }
            catch (Exception ex)
            {
                Log.Write("Apply 异常: " + ex.Message);
            }
            finally
            {
                AudioApi.Release(all);
            }
        }

        bool MuteAndRemember(AudioSession s)
        {
            if (s.Vol == null || s.Key == null) return false;
            if (!_saved.ContainsKey(s.Key))
                _saved[s.Key] = new SavedState { Mute = s.Mute };
            if (s.Mute) return false;
            s.Vol.SetMute(true);
            s.Mute = true;
            return true;
        }

        /// <summary>
        /// 让该会话出声：静音一律清掉，不“还原记忆里的静音”。
        /// v1.3.1 的根因修复：旧实现把「当前已静音」当成原状保存，恢复时又照着还原成静音，
        /// 一旦某个会话在工具看到它之前就是静音（Windows 会持久化每个应用的静音状态，
        /// 上次被本工具静音过 → 新会话一起来就是静音），就会永久锁死在静音。
        /// v1.3.2：音量逻辑彻底删除——本工具只做开关，绝不写 SetVolume。
        /// </summary>
        bool EnsureSound(AudioSession s)
        {
            if (s.Vol == null) return false;
            bool acted = false;

            if (s.Mute)
            {
                s.Vol.SetMute(false);
                s.Mute = false;
                acted = true;
            }

            if (s.Key != null) _saved.Remove(s.Key);
            return acted;
        }

        bool RestoreIfSaved(AudioSession s)
        {
            if (s.Vol == null || s.Key == null) return false;
            SavedState st;
            if (!_saved.TryGetValue(s.Key, out st)) return false;

            bool acted = false;
            if (s.Mute != st.Mute) { s.Vol.SetMute(st.Mute); acted = true; }
            _saved.Remove(s.Key);
            return acted;
        }

        void PruneSaved(HashSet<string> liveKeys)
        {
            if (_saved.Count == 0) return;
            var dead = new List<string>();
            foreach (var k in _saved.Keys) if (!liveKeys.Contains(k)) dead.Add(k);
            foreach (var k in dead) _saved.Remove(k);
        }

        void MassRestore()
        {
            var all = new List<AudioSession>();
            try
            {
                _map = InstanceMap.Build(_cfg.ProcessNames);
                _mapStamp = DateTime.UtcNow;
                all = AudioApi.Enumerate();
                foreach (var s in all)
                {
                    // 实例会话一律放开静音：不依赖 _saved，避免旧逻辑漏记导致「暂停/退出后还是哑的」
                    if (s.Vol != null && _map.OwnerOf(s.Pid) >= 0 && s.Mute) s.Vol.SetMute(false);
                    RestoreIfSaved(s);
                }
                _saved.Clear();
                Log.Write("已恢复全部实例有声");
            }
            catch (Exception ex) { Log.Write("MassRestore 异常: " + ex.Message); }
            finally { AudioApi.Release(all); }
        }

        /// <summary>启动清理：把 MuMu 实例会话的静音全部放开（修复上次异常退出的残留）</summary>
        void ResetAllInstanceMute()
        {
            var all = new List<AudioSession>();
            try
            {
                _map = InstanceMap.Build(_cfg.ProcessNames);
                _mapStamp = DateTime.UtcNow;
                all = AudioApi.Enumerate();
                foreach (var s in all)
                {
                    if (s.Vol == null) continue;
                    if (_map.OwnerOf(s.Pid) >= 0 && s.Mute)
                    {
                        s.Vol.SetMute(false);
                        Log.Write("启动清理：取消残留静音 PID=" + s.Pid);
                    }
                }
            }
            catch (Exception ex) { Log.Write("启动清理异常: " + ex.Message); }
            finally { AudioApi.Release(all); }
        }

        // ---------- 自启 ----------
        static bool IsAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
                Log.Write("开机自启 = " + on);
            }
            catch (Exception ex) { Log.Write("设置自启失败: " + ex.Message); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { MassRestore(); } catch { }
                try { DisposeIcons(); } catch { }
                try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ===================== WMI 事件订阅 =====================
    internal static class WmiInstaller
    {
        const string FilterName = "MuMuAudioRouter_Start";
        const string ConsumerName = "MuMuAudioRouter_Consumer";
        const string Query =
            "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'MuMuNxMain.exe' OR ProcessName = 'MuMuNxDevice.exe'";

        public static bool IsInstalled()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\subscription",
                    "SELECT * FROM __EventFilter WHERE Name = '" + FilterName + "'"))
                {
                    foreach (var _ in searcher.Get()) return true;
                }
            }
            catch { }
            return false;
        }

        public static void RunScript(string scriptFileName)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scriptFileName);
            if (!File.Exists(script))
                throw new FileNotFoundException("缺少脚本：" + script);

            // 脚本内容已内置目标 exe 的绝对路径，无需再传参数
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }

        public static string BuildInstallScript(string exePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$filterName   = '" + FilterName + "'");
            sb.AppendLine("$consumerName = '" + ConsumerName + "'");
            sb.AppendLine("$query = \"" + Query + "\"");
            sb.AppendLine("$exe = '" + exePath.Replace("'", "''") + "'");
            sb.AppendLine("$taskName = '" + Program.TaskName + "'");
            sb.AppendLine("$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name");
            sb.AppendLine(@"
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
$exeQuoted = '""' + $exe + '""'
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
");
            return sb.ToString();
        }

        public static string BuildUninstallScript()
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$filterName   = '" + FilterName + "'");
            sb.AppendLine("$consumerName = '" + ConsumerName + "'");
            sb.AppendLine("$taskName = '" + Program.TaskName + "'");
            sb.AppendLine(@"
Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding -ErrorAction SilentlyContinue |
  Where-Object { $_.Filter -match $filterName -or $_.Consumer -match $consumerName } |
  ForEach-Object { Remove-WmiObject -InputObject $_ -ErrorAction SilentlyContinue }
Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $consumerName } | Remove-WmiObject -ErrorAction SilentlyContinue
Get-WmiObject -Namespace root\subscription -Class __EventFilter -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq $filterName } | Remove-WmiObject -ErrorAction SilentlyContinue
schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
Write-Host 'OK: 已卸载（WMI 订阅 + 计划任务）'
");
            return sb.ToString();
        }
    }

    // ===================== 入口 =====================
    internal static class Program
    {
        public const string TaskName = "MuMuAudioRouter";

        /// <summary>
        /// WMI 的 CommandLineEventConsumer 会把进程拉进 Session 0（服务会话），
        /// 那里没有用户桌面、也没有用户的音频设备：GetForegroundWindow 恒为 0、会话枚举为空，
        /// 工具会“活着但什么都不做”。检测到就交给计划任务在用户交互会话里重新启动。
        /// </summary>
        internal static void RelaunchInUserSession()
        {
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(sys, "schtasks.exe"),
                    Arguments = "/run /tn \"" + TaskName + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("转交交互会话失败: " + ex.Message); }
        }

        [STAThread]
        static void Main()
        {
            int sid = TrayContext.SafeSessionId();
            if (sid == 0)
            {
                Log.Init(AppDomain.CurrentDomain.BaseDirectory);
                Log.Write("检测到 Session 0（非交互会话）→ 交计划任务 " + TaskName + " 在用户会话启动，本进程退出");
                RelaunchInUserSession();
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "MuMuAudioRouter_SingleInstance_v1", out created))
            {
                if (!created) return;   // 已在运行，静默返回（WMI 可能重复触发）
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += (s, e) => Log.Write("UI 异常: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理异常: " + e.ExceptionObject);

                Application.Run(new TrayContext());
            }
        }
    }

    // ===================== 生成安装脚本（供工具调用）=====================
    internal static class ScriptWriter
    {
        public static void EnsureScripts(string dir)
        {
            try
            {
                string install = Path.Combine(dir, "install-autostart.ps1");
                string uninstall = Path.Combine(dir, "uninstall-autostart.ps1");
                string exe = Path.Combine(dir, "MuMuAudioRouter.exe");
                File.WriteAllText(install, WmiInstaller.BuildInstallScript(exe), new UTF8Encoding(true));
                File.WriteAllText(uninstall, WmiInstaller.BuildUninstallScript(), new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
