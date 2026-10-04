using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WebSiteMonitor;

internal static class RestartLauncher
{
    internal const string ArgumentPrefix = "--restart-wait=";
    internal const string HandoffPrefix = "--restart-handoff=";
    internal enum WaitResult { Exited, AlreadyGone, InvalidIdentity, IdentityMismatch, TimedOut, Unavailable }

    internal static Process StartHelper(string? probeArgument = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        using var current = Process.GetCurrentProcess();
        using var handoff = RestartHandoff.Create();
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(ArgumentPrefix + current.Id.ToString(CultureInfo.InvariantCulture) + ":" + current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(HandoffPrefix + handoff.Id);
        if (probeArgument is not null) info.ArgumentList.Add(probeArgument);
        var helper = (start ?? Process.Start)(info) ?? throw new InvalidOperationException("再起動補助プロセスを開始できません。");
        // Do not permit old-process teardown until the helper pins and verifies its identity.
        // If readiness times out, no commit is sent: a late helper cannot restart on a later exit.
        if (!handoff.WaitAndCommit(TimeSpan.FromSeconds(10)))
        {
            helper.Dispose();
            throw new InvalidOperationException("再起動補助プロセスの引き継ぎを確認できません。");
        }
        return helper;
    }

    internal static async Task<bool> WaitForOldProcessAsync(int pid, long startTicks, string executable, TimeSpan? timeout = null)
        => IsSuccess(await WaitAsync(pid, startTicks, executable, timeout ?? TimeSpan.FromSeconds(30), NativeRestartProcess.Open).ConfigureAwait(false));

    internal static bool IsSuccess(WaitResult result) => result is WaitResult.Exited or WaitResult.AlreadyGone;

    // One open handle pins one process object throughout identity checking and waiting.
    // Neither module enumeration nor a second PID lookup can race its destruction/reuse.
    internal static async Task<WaitResult> WaitAsync(int pid, long ticks, string executable, TimeSpan timeout, Func<int, IRestartProcess?> open, Func<bool>? verified = null)
    {
        if (pid <= 0 || pid == Environment.ProcessId || ticks <= 0 || timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            return WaitResult.InvalidIdentity;
        try
        {
            var expectedPath = Path.GetFullPath(executable);
            using var old = open(pid);
            if (old is null) return verified is null ? WaitResult.AlreadyGone : WaitResult.Unavailable;
            if (old.StartTicks != ticks) return WaitResult.IdentityMismatch;
            string path;
            try { path = old.Executable; }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 6 or 31 or 87 or 299)
            {
                // Exit-related error AND the same signaled handle are required.
                return verified is null && old.HasExited ? WaitResult.Exited : WaitResult.Unavailable;
            }
            if (!string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase)) return WaitResult.IdentityMismatch;
            if (verified is not null && !verified()) return WaitResult.Unavailable;
            return await old.WaitForExitAsync(timeout).ConfigureAwait(false) ? WaitResult.Exited : WaitResult.TimedOut;
        }
        catch { return WaitResult.Unavailable; } // Access denied, malformed evidence and unexpected errors fail closed.
    }

    internal static WaitResult CompleteHelper(string[] args)
    {
        if (args.Length is < 1 or > 3 ||
            !args[0].StartsWith(ArgumentPrefix, StringComparison.Ordinal)) return WaitResult.InvalidIdentity;
        var handoffArgument = args.Length > 1 && args[1].StartsWith(HandoffPrefix, StringComparison.Ordinal) ? args[1] : null;
        var probeIndex = handoffArgument is null ? 1 : 2;
        if (args.Length > probeIndex + 1 || args.Length > probeIndex && !args[probeIndex].StartsWith(RestartProbeApplication.ArgumentPrefix, StringComparison.Ordinal)) return WaitResult.InvalidIdentity;
        var identity = args[0][ArgumentPrefix.Length..].Split(':');
        if (identity.Length != 2 || !int.TryParse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return WaitResult.InvalidIdentity;
        try
        {
            using var handoff = handoffArgument is null ? null : RestartHandoff.Open(handoffArgument[HandoffPrefix.Length..]);
            return WaitAsync(pid, ticks, Environment.ProcessPath ?? Application.ExecutablePath, TimeSpan.FromSeconds(30), NativeRestartProcess.Open,
                handoff is null ? null : () => handoff.SignalAndAwaitCommit(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();
        }
        catch { return WaitResult.Unavailable; }
    }

    internal static bool CompleteHelperMode(string[] args) => IsSuccess(CompleteHelper(args));

    internal sealed class RestartHandoff : IDisposable
    {
        internal string Id { get; }
        private readonly EventWaitHandle _ready;
        private readonly EventWaitHandle _commit;
        private RestartHandoff(string id, EventWaitHandle ready, EventWaitHandle commit) { Id = id; _ready = ready; _commit = commit; }
        private static string Name(string id, string stage) => "Local\\WebSiteMonitor.Restart." + id + "." + stage;
        internal static RestartHandoff Create()
        {
            var id = Guid.NewGuid().ToString("N");
            var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Name(id, "ready"), out var readyCreated);
            try
            {
                var commit = new EventWaitHandle(false, EventResetMode.ManualReset, Name(id, "commit"), out var commitCreated);
                if (!readyCreated || !commitCreated) { commit.Dispose(); throw new InvalidOperationException("再起動の排他確認に失敗しました。"); }
                return new RestartHandoff(id, ready, commit);
            }
            catch { ready.Dispose(); throw; }
        }
        internal static RestartHandoff Open(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid handoff identity");
            var ready = EventWaitHandle.OpenExisting(Name(id, "ready"));
            try { return new RestartHandoff(id, ready, EventWaitHandle.OpenExisting(Name(id, "commit"))); }
            catch { ready.Dispose(); throw; }
        }
        internal bool WaitAndCommit(TimeSpan timeout)
        {
            if (!_ready.WaitOne(timeout)) return false;
            _commit.Set(); return true;
        }
        internal bool SignalAndAwaitCommit(TimeSpan timeout) { _ready.Set(); return _commit.WaitOne(timeout); }
        public void Dispose() { _ready.Dispose(); _commit.Dispose(); }
    }

    internal interface IRestartProcess : IDisposable
    {
        long StartTicks { get; }
        string Executable { get; }
        bool HasExited { get; }
        Task<bool> WaitForExitAsync(TimeSpan timeout);
    }

    internal sealed class NativeRestartProcess(SafeProcessHandle handle) : IRestartProcess
    {
        internal static IRestartProcess? Open(int pid)
        {
            var handle = OpenProcess(0x00100000 | 0x1000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (!handle.IsInvalid) return new NativeRestartProcess(handle);
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 87 && pid > 0) return null; // ERROR_INVALID_PARAMETER: PID no longer exists.
            throw new Win32Exception(error);
        }
        public long StartTicks
        {
            get
            {
                if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return DateTime.FromFileTimeUtc(creation).Ticks;
            }
        }
        public string Executable
        {
            get
            {
                var path = new StringBuilder(32768); var length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return path.ToString();
            }
        }
        public bool HasExited => Wait(0);
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.Run(() => Wait((uint)Math.Ceiling(timeout.TotalMilliseconds)));
        private bool Wait(uint milliseconds) => WaitForSingleObject(handle, milliseconds) switch
        {
            0 => true, 258 => false, _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };
        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}
