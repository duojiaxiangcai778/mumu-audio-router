$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class ForceFG
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public static bool Force(IntPtr target)
    {
        IntPtr fg = GetForegroundWindow();
        uint fpid;
        uint tid = GetWindowThreadProcessId(fg, out fpid);
        uint me = GetCurrentThreadId();
        AttachThreadInput(me, tid, true);
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        ShowWindow(target, 9);
        bool r = SetForegroundWindow(target);
        AttachThreadInput(me, tid, false);
        return r;
    }

    public static IntPtr MainHwnd(int pid)
    {
        System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
        return p.MainWindowHandle;
    }
}
'@

$targetPid = [int]$args[0]
$h = [ForceFG]::MainHwnd($targetPid)
Write-Output ("target pid={0} hwnd=0x{1}" -f $targetPid, $h.ToInt64().ToString('X'))
if ($h -eq [IntPtr]::Zero) { Write-Output "no main window"; exit 1 }
$ok = [ForceFG]::Force($h)
Write-Output "SetForegroundWindow = $ok"
Start-Sleep -Milliseconds 600
$cur = [ForceFG]::GetForegroundWindow()
Write-Output ("now foreground hwnd=0x{0}" -f $cur.ToInt64().ToString('X'))
