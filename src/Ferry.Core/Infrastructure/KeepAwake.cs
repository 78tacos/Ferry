using System.Runtime.InteropServices;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// Stops Windows from going to sleep (including Modern Standby) while a migration runs, and from suspending Ferry
/// when the screen turns off. Uses a power request (shown in "powercfg /requests" with the reason), which, unlike
/// SetThreadExecutionState, isn't tied to one thread, so it survives async code hopping between threads.
/// Closing the lid or pressing the power button still sleeps the PC; nothing should override that.
/// </summary>
public sealed partial class KeepAwake : IDisposable
{
    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestExecutionRequired = 3;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    private readonly IntPtr handle;
    private readonly IntPtr reason;
    private bool disposed;

    private KeepAwake(IntPtr handle, IntPtr reason)
    {
        this.handle = handle;
        this.reason = reason;
    }

    public static KeepAwake Start(string why)
    {
        var reason = Marshal.StringToHGlobalUni(why);
        var context = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = reason,
        };
        var handle = PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            Log.Warn($"Could not stop the PC from sleeping (error {Marshal.GetLastPInvokeError()}). Keep it awake during the migration.");
            Marshal.FreeHGlobal(reason);
            return new KeepAwake(IntPtr.Zero, IntPtr.Zero);
        }
        PowerSetRequest(handle, PowerRequestSystemRequired);
        PowerSetRequest(handle, PowerRequestExecutionRequired);
        return new KeepAwake(handle, reason);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (handle != IntPtr.Zero)
        {
            PowerClearRequest(handle, PowerRequestSystemRequired);
            PowerClearRequest(handle, PowerRequestExecutionRequired);
            CloseHandle(handle);
        }
        if (reason != IntPtr.Zero)
            Marshal.FreeHGlobal(reason);
    }

    // REASON_CONTEXT: the reason union is as large as its "Detailed" form (pointer, ULONG, pointer), hence Size = 32.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public IntPtr SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
