#if WINDOWS
using System.Runtime.InteropServices;

namespace BA.Services;

/// <summary>
/// Weist den Prozess einem Windows-Job-Objekt mit <c>KILL_ON_JOB_CLOSE</c> zu, sodass die ffmpeg-Kindprozesse
/// beim App-Ende (auch bei Absturz) mitsterben. Ohne das bleiben Waisen liegen, die den Medien-Port belegen und
/// den nächsten Start mit einem Bind-Fehler scheitern lassen (Kindprozesse sterben scheinbar unter Windows nicht automatisch).
/// </summary>
public static class ProcessJob
{
    // Handle bewusst die ganze Laufzeit offen halten: schließt das letzte Handle, greift KILL_ON_JOB_CLOSE.
    private static IntPtr _job = IntPtr.Zero;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public static void EnsureKillOnExit()
    {
        if (_job != IntPtr.Zero)
            return;

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int len = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                {
                    CloseHandle(job);
                    return;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }

            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                CloseHandle(job);
                return;
            }

            _job = job;
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
#endif
