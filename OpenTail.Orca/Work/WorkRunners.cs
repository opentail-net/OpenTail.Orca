using System.Diagnostics;

namespace OpenTail.Orca;

/// <summary>
/// Starts the configured command when work is ready for its profile or identity (work design, step 3, option 3; off unless the policy
/// lists runners). The command gets the work id and pulls the item with <c>next_work</c> or <c>get_work</c> like any agent; the Host
/// only starts the process, the way it starts nothing else of the agent's.
/// </summary>
public sealed class WorkRunners : IDisposable
{
    private readonly WorkService _work;
    private readonly IAuditSink _audit;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly Dictionary<WorkRunner, int> _running = [];
    private readonly Lock _gate = new();

    public WorkRunners(WorkService work, IAuditSink audit, Func<ProcessStartInfo, Process?>? start = null)
    {
        _work = work;
        _audit = audit;
        _start = start ?? Process.Start;
        _work.Ready += OnReady;
    }

    private void OnReady(WorkItem item)
    {
        var runner = _work.Config.Runners.FirstOrDefault(r => r.For == item.For);
        if (runner is null) return;
        lock (_gate)
        {
            if (_running.GetValueOrDefault(runner) >= runner.MaxConcurrent) return;
            _running[runner] = _running.GetValueOrDefault(runner) + 1;
        }
        var info = new ProcessStartInfo(runner.Command) { UseShellExecute = false, RedirectStandardInput = false };
        foreach (var a in runner.Arguments) info.ArgumentList.Add(a.Replace("{workId}", item.Id, StringComparison.Ordinal));
        info.Environment["OPENTAIL_WORK_ID"] = item.Id;
        Process? p = null;
        try { p = _start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "work.runner_failed", null, null, "system", $"{item.Id}: {runner.Command}: {ex.GetType().Name}"));
        }
        if (p is null) { Release(runner); return; }
        _ = _audit.AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "work.runner_started", null, null, "system", $"{item.Id}: {runner.Command} (pid {p.Id})"));
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => { Release(runner); p.Dispose(); };
        if (p.HasExited) Release(runner);
    }

    private void Release(WorkRunner runner)
    {
        lock (_gate) _running[runner] = Math.Max(0, _running.GetValueOrDefault(runner) - 1);
    }

    public void Dispose() => _work.Ready -= OnReady;
}
