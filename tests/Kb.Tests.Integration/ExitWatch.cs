using System.Diagnostics;
using System.Runtime.InteropServices;

[assembly: AssemblyFixture(typeof(Kb.Tests.Integration.ExitWatch))]

namespace Kb.Tests.Integration;

/// <summary>
/// When every test has run, the test process must end. Sometimes it did not: the runner then waited ten seconds, wrote "Foreground
/// threads were left running" and failed a run in which every test had passed, without saying what held it. This watches for that:
/// if the process is still there a few seconds after the last test, it writes a memory dump of the process and prints every thread's
/// stack from it (through the dotnet-dump tool, which CI installs), so the run that fails names its cause. The dump is written by the
/// process itself, because in the case this is for the runtime answers no diagnostic tool. The process is kept from ending until the
/// stacks are printed. Windows only, which is where CI runs.
/// </summary>
public sealed class ExitWatch : IDisposable
{
#pragma warning disable SYSLIB1054, CA5392 // a plain import: the generated kind needs unsafe code switched on for the whole project
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeHandle file, int type, IntPtr exception, IntPtr userStream, IntPtr callback);
#pragma warning restore SYSLIB1054, CA5392

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clock = Stopwatch.StartNew();
        var reported = new ManualResetEventSlim(initialState: true);
        static void Say(string text) { Console.Error.WriteLine("[exit watch] " + text); Console.Error.Flush(); }
        // The runner ends a process that outlives its tests with Environment.Exit: that waits here until the stacks are out.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => reported.Wait(TimeSpan.FromMinutes(4));
        // A background thread: it never keeps the process alive itself.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            reported.Reset();
            try
            {
                using var self = Process.GetCurrentProcess();
                Say($"the process is still here {clock.Elapsed.TotalSeconds:F1} s after the last test, with {self.Threads.Count} threads; writing a dump");
                var path = Path.Combine(Path.GetTempPath(), $"kb-tests-{Environment.ProcessId}.dmp");
                using (var file = File.Create(path))
                    if (!MiniDumpWriteDump(self.Handle, self.Id, file.SafeFileHandle, 0x2 /* full memory */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    {
                        Say($"the dump could not be written (error {Marshal.GetLastPInvokeError()})");
                        return;
                    }
                Say($"dump written after {clock.Elapsed.TotalSeconds:F1} s ({new FileInfo(path).Length / 1_048_576} MB); reading the stacks");
                using var read = new Process { StartInfo = new ProcessStartInfo("dotnet-dump", $"analyze \"{path}\" -c \"clrthreads\" -c \"clrstack -all\" -c \"exit\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
                var text = new System.Text.StringBuilder();
                read.OutputDataReceived += (_, e) => { lock (text) text.AppendLine(e.Data); };
                read.ErrorDataReceived += (_, e) => { lock (text) text.AppendLine(e.Data); };
                read.Start();
                read.BeginOutputReadLine();
                read.BeginErrorReadLine();
                if (!read.WaitForExit(TimeSpan.FromMinutes(3))) read.Kill();
                lock (text) Say("threads when the process should have ended:\n" + text);
                File.Delete(path);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Say("the stacks could not be read: " + e.Message);
            }
            finally { reported.Set(); }
        }) { IsBackground = true, Name = "exit watch" }.Start();
    }
}
