# 读写某个进程音频会话的音量/静音（诊断工具）
# 用法：
#   powershell -ExecutionPolicy Bypass -File session_volume.ps1 -ProcessId 35128            # 只读
#   powershell -ExecutionPolicy Bypass -File session_volume.ps1 -ProcessId 35128 -SetVolume 0.42
#   powershell -ExecutionPolicy Bypass -File session_volume.ps1 -ProcessId 35128 -SetMute true
# 用途：验证 v1.3.2「只做开关、不碰音量」——工具切焦点后音量应保持调用方设定的值。
param(
    [Parameter(Mandatory = $true)][Alias('Pid')][int]$ProcessId,
    [double]$SetVolume = -1,
    [ValidateSet('true', 'false', '')][string]$SetMute = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class SessionVol
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

    // 返回值：每行一个会话的 "vol=0.42 mute=True"
    public static string Run(int wantPid, float setVol, int setMute /* -1 不变, 0 false, 1 true */)
    {
        var sb = new System.Text.StringBuilder();
        var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        IMMDeviceCollection coll;
        if (en.EnumAudioEndpoints(0, 1, out coll) != 0) return "EnumAudioEndpoints failed";
        int devCount;
        if (coll.GetCount(out devCount) != 0) return "devCount failed";
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
                int pid = -1;
                try { var c2 = (IAudioSessionControl2)ctl; c2.GetProcessId(out pid); } catch { continue; }
                if (pid != wantPid) continue;

                var vol = (ISimpleAudioVolume)ctl;
                if (setVol >= 0f) vol.SetMasterVolume(setVol, IntPtr.Zero);
                if (setMute >= 0) vol.SetMute(setMute == 1, IntPtr.Zero);

                float v; bool m;
                vol.GetMasterVolume(out v);
                vol.GetMute(out m);
                sb.AppendLine("dev[" + d + "] pid=" + pid + " vol=" + v.ToString("0.00") + " mute=" + m);
            }
        }
        if (sb.Length == 0) return "no session for pid " + wantPid;
        return sb.ToString().TrimEnd();
    }
}
'@

$sv = if ($SetVolume -lt 0) { -1.0 } else { [double]$SetVolume }
$sm = if ($SetMute -eq '') { -1 } elseif ($SetMute -eq 'true') { 1 } else { 0 }
[SessionVol]::Run($ProcessId, [float]$sv, $sm)
