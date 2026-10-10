using System.Runtime.InteropServices;

namespace ViewInFFXIV.Host;

internal static class Program
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    // Held for the helper's whole life and never closed by us: when the helper exits, even by crashing, Windows
    // closes it and ends every process in the job, so the browser started from here never outlives its helper.
    private static IntPtr job;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, ViewInFFXIV.Shared.IpcConstants.MutexName, out var created);
        if (!created)
        {
            return;
        }

        EndChildrenWithHelper();
        ApplicationConfiguration.Initialize();
        Application.Run(new HostForm(args));
    }

    /// <summary>Puts the helper in a kill-on-close job; processes it starts later join the job too.</summary>
    private static void EndChildrenWithHelper()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return;

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())
            && AssignProcessToJobObject(job, GetCurrentProcess()))
            return;

        // Not allowed here (e.g. a job that forbids nesting): the plugin still closes the browser on Stop.
        CloseHandle(job);
        job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
