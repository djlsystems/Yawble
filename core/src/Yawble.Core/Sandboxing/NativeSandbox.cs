using System.Runtime.InteropServices;
using System.Text;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// Direct access to the exact-JSON entry point of the native mxc_ffi library that ships inside the
/// Microsoft.Mxc.Sdk package. The released 1.0.1 managed SDK exposes only the typed V1 API (it has no
/// V1.Dev RunJson/ValidateProcessJson), but the native export mxc_run_json is there, and it is the parser
/// wxc-exec.exe also uses for our compiled requests.
/// Layouts mirror microsoft/mxc src/ffi/mxc_ffi/src/lib.rs and error_detail.rs at v1.0.1.
/// </summary>
public static partial class NativeSandbox
{
    private const string Library = "mxc_ffi";

    /// <summary>MXC_STATUS_* values (ErrorCode in the managed SDK).</summary>
    public const int StatusSuccess = 0;
    public const int StatusMalformedRequest = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ErrorDetail
    {
        public IntPtr Message;
        public IntPtr Operation;
        public IntPtr NativeCode;
        public IntPtr Remediation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RunResult
    {
        public int Status;
        public int ExitCode;
        public int TimedOut;
        public IntPtr Stdout;
        public IntPtr Stderr;
        public ErrorDetail Error;
        public IntPtr OutputMetadataJson;
        public IntPtr WarningsJson;
    }

    [LibraryImport(Library, EntryPoint = "mxc_run_json")]
    private static partial int RunJsonNative(byte[] requestJsonUtf8, int experimental, ref RunResult result);

    [LibraryImport(Library, EntryPoint = "mxc_run_result_free")]
    private static partial void RunResultFree(ref RunResult result);

    /// <summary>MXC_STATUS_* values used by validation (src/ffi/mxc_ffi/src/lib.rs at v1.0.1).</summary>
    public const int StatusUnsupportedContainment = 2;
    public const int StatusBackendUnavailable = 4;
    public const int StatusPolicyValidation = 11;
    public const int StatusBackendError = 12;

    // Windows builds only (lib.rs:530-608 at v1.0.1, #[cfg(target_os = "windows")]). Bound lazily, so the
    // import is harmless on Linux and macOS as long as it is never called there.
    [LibraryImport(Library, EntryPoint = "mxc_probe_request_json_with_error")]
    private static partial int ProbeRequestJsonNative(byte[] requestJsonUtf8, out IntPtr outJson, ref ErrorDetail error);

    [LibraryImport(Library, EntryPoint = "mxc_string_free")]
    private static partial void StringFree(IntPtr s);

    [LibraryImport(Library, EntryPoint = "mxc_error_detail_free")]
    private static partial void ErrorDetailFree(ref ErrorDetail detail);

    public readonly record struct ProbeOutcome(int Status, string? OutputJson, string? Message);

    /// <summary>
    /// The request-aware ProcessContainer probe: parses the exact JSON and checks it against this host's
    /// tiers without creating a sandbox. Windows only.
    /// </summary>
    public static ProbeOutcome ProbeRequestJson(string json)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("mxc_probe_request_json_with_error exists only in Windows builds of mxc_ffi 1.0.1");
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        var error = default(ErrorDetail);
        var status = ProbeRequestJsonNative(bytes, out var output, ref error);
        try
        {
            return new ProbeOutcome(status, output == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(output), Marshal.PtrToStringUTF8(error.Message));
        }
        finally
        {
            if (output != IntPtr.Zero) StringFree(output);
            ErrorDetailFree(ref error);
        }
    }

    public readonly record struct Outcome(int Status, int ExitCode, string? Message);

    /// <summary>A finished one-shot run: the sandbox captures the sandboxed process's stdout and stderr (lib.rs execute_output).</summary>
    public readonly record struct Execution(int Status, int ExitCode, bool TimedOut, string Stdout, string Stderr, string? Message);

    /// <summary>
    /// Runs one exact-JSON request to completion and returns its captured output (never with the experimental flag).
    /// Blocks the calling thread until the process exits or the request's process.timeout ends it.
    /// </summary>
    public static Execution Execute(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        var result = default(RunResult);
        var status = RunJsonNative(bytes, 0, ref result);
        try
        {
            return new Execution(status, result.ExitCode, result.TimedOut != 0,
                Marshal.PtrToStringUTF8(result.Stdout) ?? "", Marshal.PtrToStringUTF8(result.Stderr) ?? "",
                Marshal.PtrToStringUTF8(result.Error.Message));
        }
        finally
        {
            RunResultFree(ref result);
        }
    }

    /// <summary>Runs one exact-JSON one-shot request (never with the experimental flag).</summary>
    public static Outcome RunJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\0");
        var result = default(RunResult);
        var status = RunJsonNative(bytes, 0, ref result);
        try
        {
            return new Outcome(status, result.ExitCode, Marshal.PtrToStringUTF8(result.Error.Message));
        }
        finally
        {
            RunResultFree(ref result);
        }
    }
}
