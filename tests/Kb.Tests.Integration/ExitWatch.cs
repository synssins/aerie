using System.Diagnostics;

[assembly: AssemblyFixture(typeof(Kb.Tests.Integration.ExitWatch))]

namespace Kb.Tests.Integration;

/// <summary>
/// When every test has run, the test process must end. Sometimes it did not: the runner then waited ten seconds, wrote "Foreground
/// threads were left running" and failed a run in which every test had passed, without saying which thread. This watches for that:
/// if the process is still there a few seconds after the last test, it writes every managed thread's stack to the output, so the run
/// that fails names its cause. It needs the dotnet-stack tool (CI installs it) and does nothing when the tool is missing.
/// </summary>
public sealed class ExitWatch : IDisposable
{
    public void Dispose()
    {
        // A background thread: it never keeps the process alive itself.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            try
            {
                using var dump = Process.Start(new ProcessStartInfo("dotnet-stack", $"report -p {Environment.ProcessId}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (dump is null) return;
                var stacks = dump.StandardOutput.ReadToEnd() + dump.StandardError.ReadToEnd();
                Console.Error.WriteLine("The test process did not end after the last test. Managed threads:\n" + stacks);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine("The test process did not end after the last test, and dotnet-stack could not be run: " + e.Message);
            }
        }) { IsBackground = true, Name = "exit watch" }.Start();
    }
}
