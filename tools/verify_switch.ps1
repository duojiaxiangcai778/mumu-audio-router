$ErrorActionPreference = 'Stop'

# ===== 复用的音频枚举（只读） =====
$cs = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class AudioRead {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2 {
        int GetAudioSessionControl(IntPtr g, int f, out IntPtr s);
        int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr a);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr ctx);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr ctx);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr ctx);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr ctx);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier(out IntPtr r);
        int GetSessionInstanceIdentifier(out IntPtr r);
        int GetProcessId(out int pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool o);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume {
        int SetMasterVolume(float l, IntPtr c);
        int GetMasterVolume(out float l);
        int SetMute(bool m, IntPtr c);
        int GetMute(out bool m);
    }

    public static string DumpMuMu() {
        var sb = new System.Text.StringBuilder();
        var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        IMMDeviceCollection coll;
        en.EnumAudioEndpoints(0, 1, out coll);
        int dc; coll.GetCount(out dc);
        for (int d = 0; d < dc; d++) {
            IMMDevice dev; coll.Item(d, out dev);
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object o; dev.Activate(ref iid, 0, IntPtr.Zero, out o);
            IAudioSessionEnumerator ses;
            ((IAudioSessionManager2)o).GetSessionEnumerator(out ses);
            int n; ses.GetCount(out n);
            for (int i = 0; i < n; i++) {
                IAudioSessionControl c; ses.GetSession(i, out c);
                var c2 = (IAudioSessionControl2)c;
                int pid; c2.GetProcessId(out pid);
                string name;
                try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { name = "?"; }
                if (name != "MuMuNxDevice") continue;
                int st; c2.GetState(out st);
                var vol = (ISimpleAudioVolume)c;
                float v; vol.GetMasterVolume(out v);
                bool m; vol.GetMute(out m);
                sb.AppendLine(string.Format("  PID={0,-6} state={1} volume={2:0.00} mute={3}", pid, st, v, m));
            }
        }
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $cs -Language CSharp

# ===== Win32 =====
$w = @'
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
Add-Type -TypeDefinition $w -Language CSharp

function Show-State($label) {
    "=== $label ==="
    [AudioRead]::DumpMuMu()
}

# 记录原始前台窗口，方便收尾还原
$original = [W]::GetForegroundWindow()
"原前台窗口句柄: $($original.ToInt64())"

$procs = Get-Process MuMuNxDevice | Sort-Object MainWindowTitle
foreach ($p in $procs) { "实例窗口: PID=$($p.Id) 标题='$($p.MainWindowTitle)' HWND=$($p.MainWindowHandle)" }

Show-State "初始状态"

# ---- 切到实例1 ----
$p1 = $procs | Where-Object { $_.MainWindowTitle -eq '1' } | Select-Object -First 1
if ($p1) {
    [W]::ShowWindow($p1.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
    [W]::SetForegroundWindow($p1.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    Show-State "切到实例1（标题 1 / PID $($p1.Id)）后"
}

# ---- 切到实例2 ----
$p2 = $procs | Where-Object { $_.MainWindowTitle -eq '2' } | Select-Object -First 1
if ($p2) {
    [W]::ShowWindow($p2.MainWindowHandle, 9) | Out-Null
    [W]::SetForegroundWindow($p2.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    Show-State "切到实例2（标题 2 / PID $($p2.Id)）后"
}

# ---- 还原原前台窗口 ----
if ($original -ne [IntPtr]::Zero) {
    [W]::SetForegroundWindow($original) | Out-Null
    Start-Sleep -Milliseconds 900
    Show-State "切回原窗口后"
}

"--- 工具日志尾部 ---"
Get-Content "E:\MuMu实例音频路由\router.log" -Tail 12
