$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class DiagAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, int flags, out IntPtr sessionCtl);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int flags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
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
    interface IAudioSessionControl2
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
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, IntPtr eventContext);
        int GetMute(out bool mute);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    public static string ForegroundInfo()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "fg=0";
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        IntPtr root = GetAncestor(fg, 2);
        uint rpid = 0;
        if (root != IntPtr.Zero) GetWindowThreadProcessId(root, out rpid);
        StringBuilder sb = new StringBuilder(256);
        GetWindowText(root == IntPtr.Zero ? fg : root, sb, 256);
        bool vis = IsWindowVisible(root == IntPtr.Zero ? fg : root);
        return "fgHwnd=0x" + fg.ToInt64().ToString("X") + " fgPid=" + pid
            + " rootHwnd=0x" + root.ToInt64().ToString("X") + " rootPid=" + rpid
            + " visible=" + vis + " title=[" + sb.ToString() + "]";
    }

    public static string List()
    {
        StringBuilder sb = new StringBuilder();
        var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        IMMDeviceCollection coll;
        if (en.EnumAudioEndpoints(0, 1, out coll) != 0) return "EnumAudioEndpoints failed";
        int devCount;
        if (coll.GetCount(out devCount) != 0) return "devCount failed";
        sb.AppendLine("active render devices = " + devCount);
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
            sb.AppendLine("  device[" + d + "] sessions = " + n);
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl ctl;
                if (ses.GetSession(i, out ctl) != 0) continue;
                int pid = -1; int st = -1; float v = -1f; bool m = false;
                string key = "";
                try
                {
                    var c2 = (IAudioSessionControl2)ctl;
                    c2.GetProcessId(out pid);
                    c2.GetState(out st);
                    IntPtr kp;
                    if (c2.GetSessionInstanceIdentifier(out kp) == 0 && kp != IntPtr.Zero)
                    {
                        key = Marshal.PtrToStringUni(kp);
                        Marshal.FreeCoTaskMem(kp);
                    }
                }
                catch { }
                try
                {
                    var vol = (ISimpleAudioVolume)ctl;
                    vol.GetMasterVolume(out v);
                    vol.GetMute(out m);
                }
                catch { }
                string pname = "?";
                try { pname = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
                sb.AppendLine("    pid=" + pid + " (" + pname + ") state=" + st
                    + " vol=" + v.ToString("0.00") + " mute=" + m + " key=" + key);
            }
        }
        return sb.ToString();
    }
}
'@

Write-Output "===== FOREGROUND ====="
[DiagAudio]::ForegroundInfo()
Write-Output ""
Write-Output "===== AUDIO SESSIONS ====="
[DiagAudio]::List()
