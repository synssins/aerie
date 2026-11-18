using System.Diagnostics;

[assembly: AssemblyFixture(typeof(Kb.Tests.Integration.ExitWatch))]

namespace Kb.Tests.Integration;

/// <summary>
/// When every test has run, the test process must end. Sometimes it did not: the runner then waited ten seconds, wrote "Foreground
/// threads were left running" and failed a run in which every test had passed, without saying what held it. This watches for that:
/// if the process is still there a few seconds after the last test, it writes what it can find out to the output, so the run that
/// fails names its cause: whether the runtime had begun to exit, how many threads there were, and every managed thread's stack
/// (through the dotnet-stack tool, which CI installs; without the tool that part is left out).
/// </summary>
public sealed class ExitWatch : IDisposable
{
    public void Dispose()
    {
        var clock = Stopwatch.StartNew();
        static void Say(string text) { Console.Error.WriteLine("[exit watch] " + text); Console.Error.Flush(); }
        // Raised when the runtime begins to exit, which is after every foreground thread has ended.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Say($"the runtime began to exit {clock.Elapsed.TotalSeconds:F1} s after the last test");
        // A background thread: it never keeps the process alive itself.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            using var self = Process.GetCurrentProcess();
            Say($"the process is still here {clock.Elapsed.TotalSeconds:F1} s after the last test, with {self.Threads.Count} threads");
            try
            {
                using var dump = new Process { StartInfo = new ProcessStartInfo("dotnet-stack", $"report -p {Environment.ProcessId}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
                var text = new System.Text.StringBuilder();
                dump.OutputDataReceived += (_, e) => { lock (text) text.AppendLine(e.Data); };
                dump.ErrorDataReceived += (_, e) => { lock (text) text.AppendLine(e.Data); };
                dump.Start();
                dump.BeginOutputReadLine();
                dump.BeginErrorReadLine();
                var answered = dump.WaitForExit(TimeSpan.FromSeconds(6));
                lock (text) Say((answered ? "managed threads:\n" : "dotnet-stack did not answer in 6 s; what it wrote so far:\n") + text);
                if (!answered) dump.Kill();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Say("dotnet-stack could not be run: " + e.Message);
            }
        }) { IsBackground = true, Name = "exit watch" }.Start();
    }
}
