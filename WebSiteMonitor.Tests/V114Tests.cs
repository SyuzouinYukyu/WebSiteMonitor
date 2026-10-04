using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Xunit.Abstractions;
using Launcher = WebSiteMonitor.RestartLauncher;
using Result = WebSiteMonitor.RestartLauncher.WaitResult;

namespace WebSiteMonitor.Tests;

[Collection("V106 Windows Forms")]
public sealed class V114Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("normal", Result.Exited)]
    [InlineData("absent", Result.AlreadyGone)]
    [InlineData("exit-at-times", Result.Exited)]
    [InlineData("exit-at-path", Result.Exited)]
    [InlineData("exit-during-wait", Result.Exited)]
    [InlineData("reused-pid", Result.IdentityMismatch)]
    [InlineData("foreign-path", Result.IdentityMismatch)]
    [InlineData("exited-foreign-path", Result.IdentityMismatch)]
    [InlineData("open-denied", Result.Unavailable)]
    [InlineData("times-denied", Result.Unavailable)]
    [InlineData("path-denied-even-exited", Result.Unavailable)]
    [InlineData("path-299-alive", Result.Unavailable)]
    [InlineData("path-299-exited", Result.Exited)]
    [InlineData("path-87-exited", Result.Exited)]
    [InlineData("path-31-exited", Result.Exited)]
    [InlineData("path-31-alive", Result.Unavailable)]
    [InlineData("path-6-exited", Result.Exited)]
    [InlineData("unexpected-even-exited", Result.Unavailable)]
    [InlineData("exit-query-denied", Result.Unavailable)]
    [InlineData("timeout", Result.TimedOut)]
    [InlineData("wait-error", Result.Unavailable)]
    public async Task ControlledBoundaries(string scenario, object expected)
    {
        var process = new FakeProcess(scenario);
        var opens = 0;
        var actual = await Launcher.WaitAsync(123, 42, @"C:\Monitor.exe", TimeSpan.FromMilliseconds(100), _ =>
        {
            opens++;
            if (scenario == "open-denied") throw new Win32Exception(5);
            return scenario == "absent" ? null : process;
        });
        Assert.Equal((Result)expected, actual); Assert.Equal(1, opens);
        Assert.Equal(scenario is not ("absent" or "open-denied"), process.Disposed);
        if ((Result)expected == Result.IdentityMismatch || scenario is "times-denied" or "path-denied-even-exited" or "unexpected-even-exited")
            Assert.Equal(0, process.Waits);
    }

    [Theory]
    [InlineData(0, 42, 30)]
    [InlineData(-1, 42, 30)]
    [InlineData(123, 0, 30)]
    [InlineData(123, 42, -1)]
    public async Task InvalidIdentityNeverOpens(int pid, long ticks, int seconds)
    {
        Assert.Equal(Result.InvalidIdentity, await Launcher.WaitAsync(pid, ticks, @"C:\Monitor.exe", TimeSpan.FromSeconds(seconds), _ => throw new Exception("must not open")));
        Assert.False(await Launcher.WaitForOldProcessAsync(Environment.ProcessId, 42, @"C:\Monitor.exe"));
    }

    [Theory]
    [InlineData("before-times")]
    [InlineData("before-path")]
    [InlineData("during-wait")]
    public async Task RealPinnedHandleSurvivesNaturalExit(string phase)
    {
        using var child = StartChild();
        Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        var ticks = child.StartTime.ToUniversalTime().Ticks;
        var path = child.MainModule!.FileName;
        using var legacyReference = Process.GetProcessById(child.Id);
        var exited = false;
        void Exit()
        {
            if (exited) return;
            child.StandardInput.WriteLine("exit"); child.StandardInput.Flush();
            Assert.True(child.WaitForExit(10000)); Assert.Equal(0, child.ExitCode); exited = true;
        }
        try
        {
            var actual = await Launcher.WaitAsync(child.Id, ticks, path, TimeSpan.FromSeconds(10), pid =>
            {
                var pinned = Launcher.NativeRestartProcess.Open(pid)!;
                return new HookProcess(pinned, phase, Exit, output);
            });
            Assert.Equal(Result.Exited, actual); Assert.True(exited);
            // Preserve the former expression as a counterexample, outside product code.
            Exception? failure = null; var legacy = false;
            try { legacy = legacyReference.StartTime.ToUniversalTime().Ticks == ticks && string.Equals(Path.GetFullPath(legacyReference.MainModule!.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { failure = ex; }
            output.WriteLine($"phase={phase}; PID={child.Id}; naturalExit={child.ExitCode}; formerIdentity={legacy}; exception={failure?.GetType().Name}; nativeCode={(failure as Win32Exception)?.NativeErrorCode}; pinned={actual}");
            Assert.False(legacy); Assert.NotNull(failure);
        }
        finally { Exit(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperLaunchFailurePropagatesAndKeepsSafeArguments(bool throws)
    {
        var attempts = 0;
        Exception? exception = Record.Exception(() => Launcher.StartHelper(null, info =>
        {
            attempts++;
            Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
            Assert.Equal(AppContext.BaseDirectory, info.WorkingDirectory);
            Assert.Equal(Environment.ProcessPath, info.FileName);
            Assert.Equal(2, info.ArgumentList.Count); Assert.StartsWith(Launcher.ArgumentPrefix + Environment.ProcessId + ":", info.ArgumentList[0]);
            Assert.StartsWith(Launcher.HandoffPrefix, info.ArgumentList[1]);
            if (throws) throw new Win32Exception(5);
            return null;
        }));
        Assert.Equal(1, attempts);
        if (throws) Assert.IsType<Win32Exception>(exception); else Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData("--restart-wait=invalid")]
    [InlineData("--restart-wait=0:42")]
    [InlineData("--restart-wait=123:0")]
    public void MalformedHelperCannotInitialize(string argument) => Assert.False(Launcher.CompleteHelperMode([argument]));

    [Fact]
    public async Task HandoffRequiresReadyAndParentCommit()
    {
        using var parent = Launcher.RestartHandoff.Create();
        using var helper = Launcher.RestartHandoff.Open(parent.Id);
        var accepted = Task.Run(() => helper.SignalAndAwaitCommit(TimeSpan.FromSeconds(5)));
        Assert.True(parent.WaitAndCommit(TimeSpan.FromSeconds(5)));
        Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LateHelperCannotCommitAfterReadinessTimeout()
    {
        using var parent = Launcher.RestartHandoff.Create();
        using var helper = Launcher.RestartHandoff.Open(parent.Id);
        Assert.False(parent.WaitAndCommit(TimeSpan.Zero));
        Assert.False(helper.SignalAndAwaitCommit(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Launcher.RestartHandoff.Open("not-a-guid"));
    }

    [Theory]
    [InlineData("normal", true, Result.Exited, 1)]
    [InlineData("normal", false, Result.Unavailable, 1)]
    [InlineData("reused-pid", true, Result.IdentityMismatch, 0)]
    [InlineData("foreign-path", true, Result.IdentityMismatch, 0)]
    [InlineData("path-31-exited", true, Result.Unavailable, 0)]
    [InlineData("absent", true, Result.Unavailable, 0)]
    public async Task ParentCanExitOnlyAfterPinnedIdentityAndCommit(string scenario, bool committed, object expected, int signals)
    {
        var process = new FakeProcess(scenario); var calls = 0;
        var result = await Launcher.WaitAsync(123, 42, @"C:\Monitor.exe", TimeSpan.FromMilliseconds(100), _ => scenario == "absent" ? null : process,
            () => { calls++; return committed; });
        Assert.Equal((Result)expected, result); Assert.Equal(signals, calls);
        if (result != Result.Exited) Assert.Equal(0, process.Waits);
    }

    [Fact]
    public void ReentrantMainTrayRequestsCannotOpenTwoConfirmations()
    {
        Sta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WebSiteMonitor-v114-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var confirmations = 0; var starts = 0;
                using var single = new global::WebSiteMonitor.SingleInstanceCoordinator(Guid.NewGuid().ToString("N"));
                global::WebSiteMonitor.TrayApplicationContext? context = null;
                using var owned = context = new global::WebSiteMonitor.TrayApplicationContext(global::WebSiteMonitor.AppPaths.CreateAndVerify(root), single, true, true,
                    restartStarter: () => { starts++; throw new Exception("not accepted"); },
                    restartConfirmation: () =>
                    {
                        confirmations++;
                        var main = Application.OpenForms.OfType<global::WebSiteMonitor.MainForm>().Single();
                        main.Controls.OfType<ToolStrip>().SelectMany(s => s.Items.Cast<ToolStripItem>()).Single(i => i.Text == "再起動").PerformClick();
                        var tray = Get<NotifyIcon>(context!, "_tray");
                        tray.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(i => i.Text == "再起動").PerformClick();
                        return false;
                    });
                context.ShowMain();
                Restart(context).GetAwaiter().GetResult();
                Assert.Equal(1, confirmations); Assert.Equal(0, starts);
                // Guard resets after rejection, so the next independent request is accepted for confirmation.
                Restart(context).GetAwaiter().GetResult(); Assert.Equal(2, confirmations);
                typeof(global::WebSiteMonitor.TrayApplicationContext).GetField("_exiting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(context, true);
                Restart(context).GetAwaiter().GetResult(); Assert.Equal(2, confirmations); Assert.Equal(0, starts);
                typeof(global::WebSiteMonitor.TrayApplicationContext).GetField("_exiting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(context, false);
                foreach (var form in Application.OpenForms.Cast<Form>().ToArray()) form.Close();
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        });
    }

    private sealed class FakeProcess(string scenario) : Launcher.IRestartProcess
    {
        private bool exited = scenario == "exited-foreign-path";
        public bool Disposed; public int Waits;
        public long StartTicks
        {
            get
            {
                if (scenario == "times-denied") throw new Win32Exception(5);
                if (scenario == "exit-at-times") exited = true;
                return scenario == "reused-pid" ? 43 : 42;
            }
        }
        public string Executable
        {
            get
            {
                if (scenario == "exit-at-path") exited = true;
                if (scenario == "exit-query-denied") throw new Win32Exception(299);
                if (scenario == "path-denied-even-exited") { exited = true; throw new Win32Exception(5); }
                if (scenario.StartsWith("path-299")) { exited = scenario.EndsWith("exited"); throw new Win32Exception(299); }
                if (scenario == "path-87-exited") { exited = true; throw new Win32Exception(87); }
                if (scenario.StartsWith("path-31")) { exited = scenario.EndsWith("exited"); throw new Win32Exception(31); }
                if (scenario == "path-6-exited") { exited = true; throw new Win32Exception(6); }
                if (scenario == "unexpected-even-exited") { exited = true; throw new NullReferenceException(); }
                return scenario is "foreign-path" or "exited-foreign-path" ? @"C:\foreign.exe" : @"C:\Monitor.exe";
            }
        }
        public bool HasExited => scenario == "exit-query-denied" ? throw new Win32Exception(5) : exited;
        public Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            Waits++;
            if (scenario == "wait-error") throw new Win32Exception(6);
            Assert.Equal(TimeSpan.FromMilliseconds(100), timeout);
            return Task.FromResult(scenario != "timeout");
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class HookProcess(Launcher.IRestartProcess inner, string phase, Action exit, ITestOutputHelper output) : Launcher.IRestartProcess
    {
        public long StartTicks { get { if (phase == "before-times") exit(); try { return inner.StartTicks; } catch (Exception ex) { output.WriteLine("creation: " + ex); throw; } } }
        public string Executable { get { if (phase == "before-path") exit(); try { return inner.Executable; } catch (Exception ex) { output.WriteLine("image: " + ex); throw; } } }
        public bool HasExited => inner.HasExited;
        public Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var wait = inner.WaitForExitAsync(timeout);
            if (phase == "during-wait") exit();
            return wait;
        }
        public void Dispose() => inner.Dispose();
    }
    private static Process StartChild()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("[Console]::WriteLine('READY'); [Console]::ReadLine() | Out-Null");
        return Process.Start(info)!;
    }
    private static Task Restart(object context) => (Task)context.GetType().GetMethod("RestartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(context, null)!;
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(20000));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
