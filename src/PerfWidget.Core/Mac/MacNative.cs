using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PerfWidget;

/// <summary>
/// libSystem / IOKit / CoreFoundation / objc entry points and the XNU structs they fill.
/// Every struct is LP64 (identical on arm64 and x86_64); its size, from the XNU headers, is in the comment
/// next to it and checked by <see cref="LayoutProblems"/>. Libraries are absolute paths, so no resolver is needed
/// (the assembly's only resolver, in GpuSensors.cs, returns IntPtr.Zero for anything that isn't "nvml").
/// </summary>
[SupportedOSPlatform("macos")]
static unsafe class MacNative
{
    public const string LibSystem = "/usr/lib/libSystem.dylib";
    public const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    public const string CFLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string FoundationLib = "/System/Library/Frameworks/Foundation.framework/Foundation";
    public const string ObjcLib = "/usr/lib/libobjc.dylib";

    // ------------------------------------------------------------------ constants

    // sys/proc_info.h
    public const int PROC_PIDTBSDINFO = 3;
    public const int PROC_PIDTASKINFO = 4;
    public const int PROC_PIDTHREADINFO = 5;
    public const int PROC_PIDLISTTHREADS = 6;
    public const int PROC_PIDT_SHORTBSDINFO = 13;
    public const int PROC_PIDPATHINFO_MAXSIZE = 4096; // 4 * MAXPATHLEN
    // sys/resource.h
    public const int RUSAGE_INFO_V4 = 4;
    // sys/sysctl.h
    public const int CTL_KERN = 1;
    public const int KERN_ARGMAX = 8;
    public const int KERN_PROC = 14;
    public const int KERN_PROC_ALL = 0;
    public const int KERN_PROCARGS2 = 49;
    public const byte SZOMB = 5; // sys/proc.h p_stat
    // mach/host_info.h
    public const int HOST_CPU_LOAD_INFO = 3;
    public const uint HOST_CPU_LOAD_INFO_COUNT = 4;  // sizeof(host_cpu_load_info) / sizeof(integer_t)
    public const int HOST_VM_INFO64 = 4;
    public const uint HOST_VM_INFO64_COUNT = 38;     // sizeof(vm_statistics64) / sizeof(integer_t) = 152 / 4
    // mach/processor_info.h
    public const int PROCESSOR_SET_LOAD_INFO = 4;
    public const uint PROCESSOR_SET_LOAD_INFO_COUNT = 4;
    // sys/errno.h
    public const int EPERM = 1, ESRCH = 3, ENOMEM = 12;
    // unistd.h
    public const int W_OK = 2;
    // sys/mount.h
    public const uint MNT_RDONLY = 0x00000001;
    // IOKit
    public const uint kIORegistryIterateRecursively = 1, kIORegistryIterateParents = 2;
    // CoreFoundation
    public const uint kCFStringEncodingUTF8 = 0x08000100;
    public const nint kCFNumberSInt64Type = 4;

    // ------------------------------------------------------------------ structs

    /// <summary>struct proc_bsdinfo, sys/proc_info.h. 136 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcBsdInfo
    {
        public uint pbi_flags, pbi_status, pbi_xstatus, pbi_pid, pbi_ppid;
        public uint pbi_uid, pbi_gid, pbi_ruid, pbi_rgid, pbi_svuid, pbi_svgid, rfu_1;
        public fixed byte pbi_comm[16];   // MAXCOMLEN
        public fixed byte pbi_name[32];   // 2 * MAXCOMLEN
        public uint pbi_nfiles, pbi_pgid, pbi_pjobc, e_tdev, e_tpgid;
        public int pbi_nice;
        public ulong pbi_start_tvsec, pbi_start_tvusec; // offset 120
    }

    /// <summary>struct proc_bsdshortinfo, sys/proc_info.h. 64 bytes. Readable for every process (no same-user check).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcBsdShortInfo
    {
        public uint pbsi_pid, pbsi_ppid, pbsi_pgid, pbsi_status;
        public fixed byte pbsi_comm[16];
        public uint pbsi_flags, pbsi_uid, pbsi_gid, pbsi_ruid, pbsi_rgid, pbsi_svuid, pbsi_svgid, pbsi_rfu;
    }

    /// <summary>struct proc_taskinfo, sys/proc_info.h. 96 bytes. Times are mach absolute units (unused here).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcTaskInfo
    {
        public ulong pti_virtual_size, pti_resident_size, pti_total_user, pti_total_system, pti_threads_user, pti_threads_system;
        public int pti_policy, pti_faults, pti_pageins, pti_cow_faults, pti_messages_sent, pti_messages_received;
        public int pti_syscalls_mach, pti_syscalls_unix, pti_csw, pti_threadnum, pti_numrunning, pti_priority;
    }

    /// <summary>struct proc_threadinfo, sys/proc_info.h. 112 bytes. pth_user_time / pth_system_time are nanoseconds.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcThreadInfo
    {
        public ulong pth_user_time, pth_system_time;
        public int pth_cpu_usage;  // decaying average, TH_USAGE_SCALE (1000) = one full core
        public int pth_policy, pth_run_state, pth_flags, pth_sleep_time, pth_curpri, pth_priority, pth_maxpriority;
        public fixed byte pth_name[64]; // MAXTHREADNAMESIZE
    }

    /// <summary>struct rusage_info_v4, sys/resource.h. 296 bytes (16-byte uuid + 35 uint64). *_time fields are mach absolute units.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RusageInfoV4
    {
        public fixed byte ri_uuid[16];
        public ulong ri_user_time;              // 16
        public ulong ri_system_time;            // 24
        public ulong ri_pkg_idle_wkups, ri_interrupt_wkups;
        public ulong ri_pageins;                // 48
        public ulong ri_wired_size;
        public ulong ri_resident_size;          // 64
        public ulong ri_phys_footprint;         // 72
        public ulong ri_proc_start_abstime, ri_proc_exit_abstime;
        public ulong ri_child_user_time, ri_child_system_time, ri_child_pkg_idle_wkups, ri_child_interrupt_wkups;
        public ulong ri_child_pageins, ri_child_elapsed_abstime;
        public ulong ri_diskio_bytesread;       // 144
        public ulong ri_diskio_byteswritten;    // 152
        public ulong ri_cpu_time_qos_default, ri_cpu_time_qos_maintenance, ri_cpu_time_qos_background;
        public ulong ri_cpu_time_qos_utility, ri_cpu_time_qos_legacy, ri_cpu_time_qos_user_initiated;
        public ulong ri_cpu_time_qos_user_interactive;
        public ulong ri_billed_system_time, ri_serviced_system_time;
        public ulong ri_logical_writes, ri_lifetime_max_phys_footprint, ri_instructions, ri_cycles;
        public ulong ri_billed_energy, ri_serviced_energy, ri_interval_max_phys_footprint, ri_runnable_time;
    }

    /// <summary>struct vm_statistics64, mach/vm_statistics.h. 152 bytes. Counts are in kernel pages.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VmStatistics64
    {
        public uint free_count, active_count, inactive_count, wire_count;
        public ulong zero_fill_count, reactivations, pageins, pageouts, faults, cow_faults, lookups, hits, purges;
        public uint purgeable_count, speculative_count;  // 88
        public ulong decompressions, compressions, swapins, swapouts;  // swapins at 112
        public uint compressor_page_count, throttled_count, external_page_count, internal_page_count; // 128
        public ulong total_uncompressed_pages_in_compressor; // 144
    }

    /// <summary>struct host_cpu_load_info, mach/host_info.h. 16 bytes: ticks for user, system, idle, nice.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HostCpuLoadInfo { public uint user, system, idle, nice; }

    /// <summary>struct processor_set_load_info, mach/processor_info.h. 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorSetLoadInfo { public int task_count, thread_count, load_average, mach_factor; }

    /// <summary>struct xsw_usage, sys/sysctl.h. 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XswUsage { public ulong xsu_total, xsu_avail, xsu_used; public uint xsu_pagesize; public int xsu_encrypted; }

    /// <summary>struct mach_timebase_info, mach/mach_time.h. 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MachTimebaseInfo { public uint numer, denom; }

    /// <summary>
    /// struct kinfo_proc (sys/sysctl.h) = struct extern_proc (296) + struct eproc (352) = 648 bytes on LP64.
    /// Only the fields used are declared, at their LP64 offsets:
    /// extern_proc: p_starttime (struct timeval, 16) @0, p_vmspace @16, p_sigacts @24, p_flag @32, p_stat @36,
    /// p_pid @40, ..., p_comm[MAXCOMLEN+1] @243; eproc @296: e_pcred @312 (104), e_ucred @416 (cr_uid @420), e_vm @496 (64), e_ppid @560.
    /// Same offsets as golang.org/x/sys/unix KinfoProc for darwin/arm64 and darwin/amd64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 648)]
    public struct KinfoProc
    {
        [FieldOffset(0)] public long p_starttime_sec;
        [FieldOffset(8)] public int p_starttime_usec;
        [FieldOffset(36)] public byte p_stat;
        [FieldOffset(40)] public int p_pid;
        [FieldOffset(243)] public fixed byte p_comm[17];
        [FieldOffset(420)] public uint cr_uid;
        [FieldOffset(560)] public int e_ppid;
    }

    /// <summary>struct statfs with 64-bit inodes (sys/mount.h). 2168 bytes. Only f_flags (@64) is used.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 2168)]
    public struct StatFs64
    {
        [FieldOffset(8)] public ulong f_blocks;
        [FieldOffset(64)] public uint f_flags;
        [FieldOffset(72)] public fixed byte f_fstypename[16];
    }

    /// <summary>Struct sizes that don't match the XNU headers (empty when all is well).</summary>
    public static string LayoutProblems()
    {
        var sb = new StringBuilder();
        void Check(string name, int actual, int expected)
        {
            if (actual != expected) sb.Append($"{name} is {actual} bytes, XNU says {expected}; ");
        }
        Check("proc_bsdinfo", sizeof(ProcBsdInfo), 136);
        Check("proc_bsdshortinfo", sizeof(ProcBsdShortInfo), 64);
        Check("proc_taskinfo", sizeof(ProcTaskInfo), 96);
        Check("proc_threadinfo", sizeof(ProcThreadInfo), 112);
        Check("rusage_info_v4", sizeof(RusageInfoV4), 296);
        Check("vm_statistics64", sizeof(VmStatistics64), 152);
        Check("host_cpu_load_info", sizeof(HostCpuLoadInfo), 16);
        Check("processor_set_load_info", sizeof(ProcessorSetLoadInfo), 16);
        Check("xsw_usage", sizeof(XswUsage), 32);
        Check("mach_timebase_info", sizeof(MachTimebaseInfo), 8);
        Check("kinfo_proc", sizeof(KinfoProc), 648);
        Check("statfs", sizeof(StatFs64), 2168);
        Check("rusage_info_v4.ri_phys_footprint", (int)Marshal.OffsetOf<RusageInfoV4>(nameof(RusageInfoV4.ri_phys_footprint)), 72);
        Check("rusage_info_v4.ri_diskio_bytesread", (int)Marshal.OffsetOf<RusageInfoV4>(nameof(RusageInfoV4.ri_diskio_bytesread)), 144);
        Check("vm_statistics64.swapins", (int)Marshal.OffsetOf<VmStatistics64>(nameof(VmStatistics64.swapins)), 112);
        Check("vm_statistics64.compressor_page_count", (int)Marshal.OffsetOf<VmStatistics64>(nameof(VmStatistics64.compressor_page_count)), 128);
        Check("proc_bsdinfo.pbi_start_tvsec", (int)Marshal.OffsetOf<ProcBsdInfo>(nameof(ProcBsdInfo.pbi_start_tvsec)), 120);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ libSystem

    [DllImport(LibSystem, SetLastError = true)] public static extern int proc_listallpids(int* buffer, int buffersize);
    [DllImport(LibSystem, SetLastError = true)] public static extern int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int buffersize);
    [DllImport(LibSystem, SetLastError = true)] public static extern int proc_pid_rusage(int pid, int flavor, void* buffer);
    [DllImport(LibSystem, SetLastError = true)] public static extern int proc_pidpath(int pid, byte* buffer, uint buffersize);
    [DllImport(LibSystem, SetLastError = true)] public static extern int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);
    [DllImport(LibSystem, SetLastError = true)] public static extern int sysctlbyname(byte* name, void* oldp, nuint* oldlenp, void* newp, nuint newlen);
    [DllImport(LibSystem)] public static extern int mach_timebase_info(MachTimebaseInfo* info);
    [DllImport(LibSystem)] public static extern uint mach_host_self();
    [DllImport(LibSystem)] public static extern int host_statistics(uint host, int flavor, int* info, uint* count);
    [DllImport(LibSystem)] public static extern int host_statistics64(uint host, int flavor, int* info, uint* count);
    [DllImport(LibSystem)] public static extern int processor_set_default(uint host, uint* pset);
    [DllImport(LibSystem)] public static extern int processor_set_statistics(uint pset, int flavor, int* info, uint* count);
    [DllImport(LibSystem)] public static extern int getppid();
    [DllImport(LibSystem, SetLastError = true)] public static extern int access(byte* path, int mode);
    // arm64 has only the 64-bit-inode statfs; on x86_64 the plain symbol is the legacy 32-bit-inode layout.
    [DllImport(LibSystem, EntryPoint = "statfs", SetLastError = true)] static extern int statfs_arm64(byte* path, StatFs64* buf);
    [DllImport(LibSystem, EntryPoint = "statfs$INODE64", SetLastError = true)] static extern int statfs_x64(byte* path, StatFs64* buf);

    public static int StatFs(byte* path, StatFs64* buf) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? statfs_x64(path, buf) : statfs_arm64(path, buf);

    /// <summary>NUL-terminated UTF-8, for passing names to C.</summary>
    public static byte[] Z(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    /// <summary>An integer sysctl by name, whether the kernel returns 4 or 8 bytes. Null on failure.</summary>
    public static long? SysctlLong(string name)
    {
        long v = 0;
        nuint len = 8;
        fixed (byte* n = Z(name))
        {
            if (sysctlbyname(n, &v, &len, null, 0) != 0) return null;
        }
        return len switch { 8 => v, 4 => (int)v, _ => null }; // little-endian: a 4-byte answer is the low half
    }

    public static bool SysctlStruct<T>(byte[] name, out T value) where T : unmanaged
    {
        T v = default;
        nuint len = (nuint)sizeof(T);
        int r;
        fixed (byte* n = name) r = sysctlbyname(n, &v, &len, null, 0);
        value = v;
        return r == 0 && len == (nuint)sizeof(T);
    }

    public static string CString(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return n == 0 ? "" : Encoding.UTF8.GetString(p, n);
    }

    // ------------------------------------------------------------------ IOKit

    [DllImport(IOKitLib)] public static extern IntPtr IOServiceMatching(byte* name);
    [DllImport(IOKitLib)] public static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, uint* iterator);
    [DllImport(IOKitLib)] public static extern uint IOIteratorNext(uint iterator);
    [DllImport(IOKitLib)] public static extern int IOObjectRelease(uint obj);
    [DllImport(IOKitLib)] public static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IOKitLib)] public static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, byte* plane, IntPtr key, IntPtr allocator, uint options);

    /// <summary>All registry entries of an IOKit class. The caller releases each with IOObjectRelease.</summary>
    public static System.Collections.Generic.List<uint> Services(string cls)
    {
        var list = new System.Collections.Generic.List<uint>();
        IntPtr match;
        fixed (byte* c = Z(cls)) match = IOServiceMatching(c);
        if (match == IntPtr.Zero) return list;
        uint it = 0;
        // consumes one reference to `match`, even on failure; port 0 = kIOMainPortDefault
        if (IOServiceGetMatchingServices(0, match, &it) != 0 || it == 0) return list;
        try
        {
            uint e;
            while ((e = IOIteratorNext(it)) != 0) list.Add(e);
        }
        finally { IOObjectRelease(it); }
        return list;
    }

    // ------------------------------------------------------------------ CoreFoundation

    [DllImport(CFLib)] public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* cStr, uint encoding);
    [DllImport(CFLib)] public static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CFLib)] public static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CFLib)] public static extern nuint CFNumberGetTypeID();
    [DllImport(CFLib)] public static extern nuint CFDictionaryGetTypeID();
    [DllImport(CFLib)] public static extern nuint CFStringGetTypeID();
    [DllImport(CFLib)] public static extern nuint CFDataGetTypeID();
    [DllImport(CFLib)] public static extern byte CFNumberGetValue(IntPtr number, nint type, void* value);
    [DllImport(CFLib)] public static extern byte CFStringGetCString(IntPtr s, byte* buf, nint size, uint encoding);
    [DllImport(CFLib)] public static extern nint CFDataGetLength(IntPtr d);
    [DllImport(CFLib)] public static extern byte* CFDataGetBytePtr(IntPtr d);
    [DllImport(CFLib)] public static extern void CFRelease(IntPtr cf);

    public static IntPtr CFStr(string s)
    {
        fixed (byte* p = Z(s)) return CFStringCreateWithCString(IntPtr.Zero, p, kCFStringEncodingUTF8);
    }
}

/// <summary>CFTypeIDs and typed reads from CF objects (none of these retain or release).</summary>
[SupportedOSPlatform("macos")]
sealed unsafe class CF
{
    readonly nuint numberT, dictT, stringT, dataT;

    public CF()
    {
        try
        {
            numberT = MacNative.CFNumberGetTypeID();
            dictT = MacNative.CFDictionaryGetTypeID();
            stringT = MacNative.CFStringGetTypeID();
            dataT = MacNative.CFDataGetTypeID();
        }
        catch { } // CoreFoundation missing: every read below then reports "not found"
    }

    public bool IsDict(IntPtr o) => o != IntPtr.Zero && dictT != 0 && MacNative.CFGetTypeID(o) == dictT;

    /// <summary>Integer value of a CFNumber in a dictionary. Floats are truncated (CFNumberGetValue reports
    /// lossy conversions as false but still stores the best value, which is what we want).</summary>
    public bool Long(IntPtr dict, IntPtr key, out long v)
    {
        v = 0;
        if (dict == IntPtr.Zero || key == IntPtr.Zero) return false;
        IntPtr n = MacNative.CFDictionaryGetValue(dict, key);
        if (n == IntPtr.Zero || numberT == 0 || MacNative.CFGetTypeID(n) != numberT) return false;
        long x = 0;
        MacNative.CFNumberGetValue(n, MacNative.kCFNumberSInt64Type, &x);
        v = x;
        return true;
    }

    /// <summary>A CFString, or CFData holding (NUL-terminated) text, as a string.</summary>
    public string? Text(IntPtr o)
    {
        if (o == IntPtr.Zero || stringT == 0) return null;
        nuint t = MacNative.CFGetTypeID(o);
        if (t == stringT)
        {
            byte* buf = stackalloc byte[512];
            return MacNative.CFStringGetCString(o, buf, 512, MacNative.kCFStringEncodingUTF8) != 0 ? MacNative.CString(buf, 512) : null;
        }
        if (t == dataT)
        {
            nint len = MacNative.CFDataGetLength(o);
            byte* p = MacNative.CFDataGetBytePtr(o);
            return p != null && len > 0 ? MacNative.CString(p, (int)Math.Min(len, 512)) : null;
        }
        return null;
    }
}

/// <summary>[[NSProcessInfo processInfo] thermalState] through the objc runtime. All calls are function pointers.</summary>
[SupportedOSPlatform("macos")]
sealed unsafe class ThermalState
{
    readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> sendId;
    readonly delegate* unmanaged<IntPtr, IntPtr, nint> sendInt;
    readonly delegate* unmanaged<IntPtr> poolPush;
    readonly delegate* unmanaged<IntPtr, void> poolPop;
    readonly IntPtr cls, selProcessInfo, selThermalState;
    public readonly bool Ok;

    public ThermalState()
    {
        try
        {
            // NSProcessInfo lives in Foundation, which a plain .NET process hasn't necessarily loaded.
            NativeLibrary.TryLoad(MacNative.FoundationLib, out _);
            if (!NativeLibrary.TryLoad(MacNative.ObjcLib, out var objc) && !NativeLibrary.TryLoad("/usr/lib/libobjc.A.dylib", out objc)) return;
            if (!NativeLibrary.TryGetExport(objc, "objc_getClass", out var getClass) ||
                !NativeLibrary.TryGetExport(objc, "sel_registerName", out var selReg) ||
                !NativeLibrary.TryGetExport(objc, "objc_msgSend", out var msgSend)) return;
            if (NativeLibrary.TryGetExport(objc, "objc_autoreleasePoolPush", out var push) &&
                NativeLibrary.TryGetExport(objc, "objc_autoreleasePoolPop", out var pop))
            {
                poolPush = (delegate* unmanaged<IntPtr>)push;
                poolPop = (delegate* unmanaged<IntPtr, void>)pop;
            }
            var gc = (delegate* unmanaged<byte*, IntPtr>)getClass;
            var sr = (delegate* unmanaged<byte*, IntPtr>)selReg;
            fixed (byte* n = MacNative.Z("NSProcessInfo")) cls = gc(n);
            fixed (byte* n = MacNative.Z("processInfo")) selProcessInfo = sr(n);
            fixed (byte* n = MacNative.Z("thermalState")) selThermalState = sr(n);
            // objc_msgSend must be called through a pointer of the exact non-variadic prototype (required on arm64)
            sendId = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)msgSend;
            sendInt = (delegate* unmanaged<IntPtr, IntPtr, nint>)msgSend;
            Ok = cls != IntPtr.Zero && selProcessInfo != IntPtr.Zero && selThermalState != IntPtr.Zero;
        }
        catch { Ok = false; }
    }

    /// <summary>NSProcessInfoThermalState: 0 nominal, 1 fair, 2 serious, 3 critical. Null if unavailable.</summary>
    public int? Read()
    {
        if (!Ok) return null;
        IntPtr pool = poolPush != null ? poolPush() : IntPtr.Zero;
        try
        {
            IntPtr pi = sendId(cls, selProcessInfo);
            if (pi == IntPtr.Zero) return null;
            nint st = sendInt(pi, selThermalState);
            return st is >= 0 and <= 3 ? (int)st : null;
        }
        finally { if (pool != IntPtr.Zero) poolPop(pool); }
    }
}
