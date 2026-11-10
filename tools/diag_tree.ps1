$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class DiagTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
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
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public static string Dump(string[] matchNames)
    {
        StringBuilder sb = new StringBuilder();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == new IntPtr(-1)) return "snapshot failed";
        var parentOf = new Dictionary<int, int>();
        var nameOf = new Dictionary<int, string>();
        var e = new PROCESSENTRY32W();
        e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
        bool ok = Process32FirstW(snap, ref e);
        while (ok)
        {
            int pid = (int)e.th32ProcessID;
            parentOf[pid] = (int)e.th32ParentProcessID;
            nameOf[pid] = e.szExeFile;
            ok = Process32NextW(snap, ref e);
        }
        CloseHandle(snap);

        sb.AppendLine("snapshot procs = " + nameOf.Count);
        sb.AppendLine("--- MuMu 相关进程（名字原样）---");
        foreach (var kv in nameOf)
        {
            if (kv.Value.IndexOf("MuMu", StringComparison.OrdinalIgnoreCase) >= 0
                || kv.Value.IndexOf("Nemu", StringComparison.OrdinalIgnoreCase) >= 0
                || kv.Value.IndexOf("vkms", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int par = -1;
                parentOf.TryGetValue(kv.Key, out par);
                string pname = "?";
                nameOf.TryGetValue(par, out pname);
                sb.AppendLine("pid=" + kv.Key + " name=[" + kv.Value + "] parent=" + par + " (" + pname + ")");
            }
        }

        // 模拟 Build
        var children = new Dictionary<int, List<int>>();
        foreach (var kv in parentOf)
        {
            List<int> list;
            if (!children.TryGetValue(kv.Value, out list)) { list = new List<int>(); children[kv.Value] = list; }
            list.Add(kv.Key);
        }
        var instances = new Dictionary<int, HashSet<int>>();
        sb.AppendLine("--- 模拟 InstanceMap.Build 匹配结果 ---");
        foreach (var kv in nameOf)
        {
            bool hit = false;
            foreach (string p in matchNames)
            {
                if (string.Equals(kv.Value, p, StringComparison.OrdinalIgnoreCase)) hit = true;
            }
            if (!hit) continue;
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
            instances[root] = set;
            sb.Append("实例 root=" + root + " 成员: ");
            foreach (int m in set) sb.Append(m + " ");
            sb.AppendLine();
        }
        if (instances.Count == 0) sb.AppendLine("!!! 没有任何进程匹配 [" + string.Join(",", matchNames) + "]");

        foreach (int probe in new int[] { 16500 })
        {
            int owner = -1;
            foreach (var kv in instances) if (kv.Value.Contains(probe)) { owner = kv.Key; break; }
            sb.AppendLine("OwnerOf(" + probe + ") = " + owner);
        }
        return sb.ToString();
    }
}
'@

Write-Output "===== 模拟工具进程树匹配（processNames=['MuMuNxDevice']）====="
[DiagTree]::Dump(@('MuMuNxDevice'))
Write-Output ""
Write-Output "===== 当前 exe 目标框架 ====="
$p = Get-Process MuMuAudioRouter -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) { Write-Output ("工具进程位数: {0}" -f $(if ([Environment]::Is64BitOperatingSystem) { "OS64" } else { "OS32" })) }
Write-Output ("当前 powershell 位数: {0}" -f [Environment]::Is64BitProcess)
