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

    private const string CommitMessage = "Auto-refresh growth-bankroll export (app)";

    private readonly Action<string>? _log;
    private readonly string? _docsCsv;
    private System.Threading.Timer? _timer;

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
    /// for the path). Everything else — clean file, feature branch — skips.</summary>
    internal static bool ShouldAttemptPush(string? branch, string porcelain) =>
        branch == "main" && !string.IsNullOrWhiteSpace(porcelain);

    /// <summary>One full publish cycle: check → add → commit → push. Swallows
    /// every failure into a log line; the next tick retries.</summary>
    internal void PushOnce()
    {
        try
        {
            if (_docsCsv is null || !File.Exists(_docsCsv)) return;
            var repo = Path.GetDirectoryName(Path.GetDirectoryName(_docsCsv))!;
            if (!Directory.Exists(Path.Combine(repo, ".git"))) return;

            var branch = Git(repo, "rev-parse --abbrev-ref HEAD");
            if (branch is null) return;

            var porcelain = Git(repo, $"status --porcelain -- \"{_docsCsv}\"") ?? "";
            if (!ShouldAttemptPush(branch.Trim(), porcelain))
            {
                return;
            }

            Git(repo, $"add -- \"{_docsCsv}\"");
            var commit = Git(repo, $"commit -m \"{CommitMessage}\" -- \"{_docsCsv}\"");
            if (commit is null)
            {
                _log?.Invoke("bankroll publish: commit failed (will retry)");
                return;
            }

            var push = Git(repo, "push origin HEAD:main");
            _log?.Invoke(push is null
                ? "bankroll publish: push failed (will retry next tick)"
                : "bankroll publish: pushed refreshed growth-bankroll.csv to main");
        }
        catch (Exception e)
        {
            _log?.Invoke($"bankroll publish skipped: {e.Message}");
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
