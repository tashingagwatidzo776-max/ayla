using System.Diagnostics;
using System.IO;

namespace Tf.App.Infrastructure;

/// <summary>
/// Publishes the growth-bankroll export to GitHub without a human in the
/// loop. CI runners can never reach the machine where the app trades, so the
/// only automated channel from the freshly written
/// <c>docs/growth-bankroll.csv</c> to the Pages deploy is git itself: on a
/// periodic tick, if the committed export differs from the store-derived one,
/// commit and push exactly that one file.
///
/// Guardrails: only ever touches the single docs CSV pathspec (never sweeps
/// unrelated staged work), only pushes when the checkout is on <c>main</c>,
/// skips silently outside a repo checkout or without git, treats lock and
/// network failures as retry-next-tick, and never throws — chart publishing
/// must not be able to break trading.
/// </summary>
public sealed class BankrollCsvPublisher : IDisposable
{
    /// <summary>Minimum time between publish attempts. Batched so a burst of
    /// settled trades produces at most one commit per interval.</summary>
    public TimeSpan PushInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Raised when a publish attempt fails (any thread) — once per
    /// broken stretch, not per tick, so a persistently broken publish cannot
    /// spam observers while still being impossible to miss. The dashboard
    /// risk rail latches on this (toast + webhook fire from there) until
    /// <see cref="PublishRecovered"/> releases it.</summary>
    public event Action<string>? PublishFailed;

    /// <summary>Raised when a publish succeeds after a reported failure
    /// (any thread) — releases the risk-rail alert.</summary>
    public event Action<string>? PublishRecovered;

    private const string CommitMessage = "Auto-refresh growth-bankroll export (app)";

    private readonly Action<string>? _log;
    private readonly string? _docsCsv;
    private System.Threading.Timer? _timer;
    private bool _failingReported;
    private bool _pendingPush; // a commit landed but its push did not: retry the push even on a clean tree

    /// <summary>Reports a broken publish once per broken stretch: repeated
    /// failing ticks add nothing (the rail is latched already), and the first
    /// success after a reported failure raises recovery. Observers are
    /// isolated — a throwing handler must never break the publish cycle.</summary>
    private void ReportPublishOutcome(bool failed, string detail)
    {
        if (failed == _failingReported)
        {
            return; // no state change → nothing to announce
        }

        _failingReported = failed;
        try
        {
            if (failed)
            {
                PublishFailed?.Invoke(detail);
            }
            else
            {
                PublishRecovered?.Invoke(detail);
            }
        }
        catch
        {
            // Risk-rail plumbing must never break chart publishing.
        }
    }

    public BankrollCsvPublisher(string? docsCsv, Action<string>? log = null)
    {
        _docsCsv = docsCsv;
        _log = log;
    }

    /// <summary>Starts the periodic publish tick (initial attempt after one
    /// minute so startup I/O settles). No-op outside a repo checkout.</summary>
    public void Start()
    {
        if (_docsCsv is null || _timer is not null) return;
        var interval = PushInterval;
        _timer = new System.Threading.Timer(_ => TryPublish(), null,
            dueTime: TimeSpan.FromMinutes(1), period: interval);
    }

    /// <summary>True when the publish cycle should run: the checkout is on
    /// main and git reports the export differs from HEAD (porcelain non-empty
    /// for the path) — or a committed export's push is still pending, which
    /// must be retried even on a clean tree or a failed push would only ever
    /// be revisited when the next settlement re-dirties the file. Everything
    /// else — clean file, feature branch — skips.</summary>
    internal static bool ShouldAttemptPush(string? branch, string porcelain, bool pendingPush = false) =>
        branch == "main" && (!string.IsNullOrWhiteSpace(porcelain) || pendingPush);

    /// <summary>One full publish cycle: check → add → commit → push. Failures
    /// are reported once per broken stretch via <see cref="PublishFailed"/>
    /// (the dashboard risk rail latches on them); the next tick retries. The
    /// pulse file (<c>docs/growth-pulse.json</c>, the watchdog's frozen-axis
    /// ground truth) is committed alongside the CSV: it is written by the same
    /// export refresh and its content only changes when a new settled trade
    /// lands, so the publish cycle stays one commit per settlement.</summary>
    internal void PushOnce()
    {
        try
        {
            if (_docsCsv is null || !File.Exists(_docsCsv)) return;
            var repo = Path.GetDirectoryName(Path.GetDirectoryName(_docsCsv))!;
            if (!Directory.Exists(Path.Combine(repo, ".git"))) return;

            var pulse = Tf.Core.Analytics.BankrollCsvFile.PulsePathFor(_docsCsv);
            // The pulse rides the commit only when it exists: the export
            // refresh writes it beside the CSV in the real app, but a checkout
            // without it (or a direct publisher construction) must still be
            // able to publish the CSV — an unmatched pathspec would fail the
            // add/commit outright and leave the refreshed export uncommitted.
            var paths = File.Exists(pulse)
                ? $"\"{_docsCsv}\" \"{pulse}\""
                : $"\"{_docsCsv}\"";
            var branch = Git(repo, "rev-parse --abbrev-ref HEAD");
            if (branch is null) return;

            var porcelain = Git(repo, $"status --porcelain -- {paths}") ?? "";
            if (!ShouldAttemptPush(branch.Trim(), porcelain, _pendingPush))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(porcelain))
            {
                Git(repo, $"add -- {paths}");
                var commit = Git(repo, $"commit -m \"{CommitMessage}\" -- {paths}");
                if (commit is null)
                {
                    _log?.Invoke("bankroll publish: commit failed (will retry)");
                    ReportPublishOutcome(failed: true, "git commit failed — the refreshed export is not on main");
                    return;
                }
            }

            var push = Git(repo, "push origin HEAD:main");
            if (push is null)
            {
                _pendingPush = true; // committed locally; the push retries next tick even on a clean tree
                _log?.Invoke("bankroll publish: push failed (will retry next tick)");
                ReportPublishOutcome(failed: true, "git push to main failed — the Pages deploy will not see the refreshed export");
                return;
            }

            _pendingPush = false;
            _log?.Invoke("bankroll publish: pushed refreshed growth-bankroll.csv to main");
            ReportPublishOutcome(failed: false, "pushed refreshed growth-bankroll.csv to main");
        }
        catch (Exception e)
        {
            _log?.Invoke($"bankroll publish skipped: {e.Message}");
            ReportPublishOutcome(failed: true, $"publish cycle failed: {e.Message}");
        }
    }

    private void TryPublish()
    {
        try
        {
            PushOnce();
        }
        catch
        {
            // Timer callbacks must never throw.
        }
    }

    /// <summary>Runs git in <paramref name="repo"/>; null on any failure
    /// (missing git, non-zero exit, timeout, lock contention).</summary>
    private static string? Git(string repo, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repo,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000) || p.ExitCode != 0) return null;
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _timer?.Dispose();
}
