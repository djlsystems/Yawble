using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yawble.Core.Launch;

/// <summary>
/// A process on a Windows pseudo console (ConPTY) that the core owns: the sandbox's native PTY call refuses
/// ProcessContainer, so an interactive run is <c>wxc-exec.exe --config &lt;file&gt;</c> started inside a ConPTY, as
/// the sandbox's Node SDK does (microsoft/mxc sdk/node/src/bindings/process-container-pty.ts). The sandboxed child
/// shares the console.
/// <para>The process is created suspended, put in a job the core owns (kill-on-close, so the whole tree, the sandbox's
/// own nested job included, ends on Kill or when the core dies), then resumed: nothing it starts can escape the job.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyProcess : IDisposable
{
    private readonly IntPtr _pseudoConsole;
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private int _disposed;

    public int Id { get; }

    /// <summary>The terminal's output (what the program writes, VT-encoded by ConPTY).</summary>
    public Stream Output => _output;

    /// <summary>The terminal's input (keystrokes, VT input sequences).</summary>
    public Stream Input => _input;

    private ConPtyProcess(IntPtr pseudoConsole, SafeProcessHandle process, int id, SafeFileHandle job, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _process = process;
        Id = id;
        _job = job;
        _input = input;
        _output = output;
    }

    /// <summary>Starts <paramref name="commandLine"/> (an exact Windows command line) on a new pseudo console.</summary>
    public static ConPtyProcess Start(string commandLine, PtySize size, string? workingDirectory = null)
    {
        if (!CreatePipe(out var ptyInRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var ptyOutWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var hr = CreatePseudoConsole(new Coord(size.Cols, size.Rows), ptyInRead, ptyOutWrite, 0, out var pc);
        // The pseudo console holds its own duplicates of its ends of the pipes.
        ptyInRead.Dispose();
        ptyOutWrite.Dispose();
        if (hr != 0) { inputWrite.Dispose(); outputRead.Dispose(); throw new Win32Exception(hr, "CreatePseudoConsole failed"); }

        var attrList = IntPtr.Zero;
        SafeFileHandle? job = null;
        try
        {
            var attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributePseudoConsole, pc, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new StartupInfoEx { StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>() }, lpAttributeList = attrList };
            // Explicitly no std handles: without this the child can inherit the core's own redirected stdio instead of
            // the pseudo console (the core runs with piped stdio under the Electron shell).
            si.StartupInfo.dwFlags = StartfUseStdHandles;

            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref si, out var pi))
                throw new Win32Exception();
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            var thread = new SafeProcessHandle(pi.hThread, ownsHandle: true);
            try
            {
                job = CreateJob();
                if (!AssignProcessToJobObject(job, process)) throw new Win32Exception();
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }
            finally { thread.Dispose(); }

            return new ConPtyProcess(pc, process, pi.dwProcessId, job,
                new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 4096));
        }
        catch
        {
            job?.Dispose();
            ClosePseudoConsole(pc);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    /// <summary>Exit code once the process has ended; null while it runs.</summary>
    public int? TryGetExitCode()
    {
        if (WaitForSingleObject(_process, 0) != 0) return null;
        return GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;
    }

    /// <summary>
    /// Ends the whole job (the process and everything in it, the sandbox's nested job included), and waits up to 5 s for the
    /// process to be gone (TerminateJobObject is asynchronous), so the exit is observable on return.
    /// </summary>
    public void Kill()
    {
        if (TryGetExitCode() is not null && JobIsEmpty()) return;
        TerminateJobObject(_job, 1);
        WaitForSingleObject(_process, 5000);
    }

    public void Resize(PtySize size) => ResizePseudoConsole(_pseudoConsole, new Coord(size.Cols, size.Rows));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Kill(); } catch (Win32Exception) { }
        _input.Dispose();
        // ClosePseudoConsole can wait for its output to drain on some builds: never on the caller's thread for long.
        var pc = _pseudoConsole;
        var closed = Task.Run(() => ClosePseudoConsole(pc));
        closed.Wait(TimeSpan.FromSeconds(2));
        _output.Dispose();
        _job.Dispose();
        _process.Dispose();
    }

    private bool JobIsEmpty()
    {
        var info = new JobBasicAccounting();
        return QueryInformationJobObject(_job, 1 /* JobObjectBasicAccountingInformation */, ref info, Marshal.SizeOf<JobBasicAccounting>(), IntPtr.Zero)
               && info.ActiveProcesses == 0;
    }

    private static SafeFileHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new JobExtendedLimit { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref limits, Marshal.SizeOf<JobExtendedLimit>()))
            throw new Win32Exception();
        return job;
    }

    // ---- Win32

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(ushort x, ushort y)
    {
        public readonly short X = (short)x;
        public readonly short Y = (short)y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pc);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr pc, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeProcessHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref JobExtendedLimit info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int cls, ref JobBasicAccounting info, int size, IntPtr ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
}
