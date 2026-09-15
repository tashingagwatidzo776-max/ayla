using System.Diagnostics;
using System.IO;
using Tf.App.Infrastructure;

namespace Tf.App.Tests;

[Trait("Category", "Unit")]
public class BankrollCsvPublisherTests : IDisposable
{
    private readonly string _dir;
    private readonly string _repo;
    private readonly string _docsCsv;

    public BankrollCsvPublisherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bankroll-pub-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        _docsCsv = Path.Combine(_repo, "docs", "growth-bankroll.csv");
        Git($"init \"{_repo}\"");
        Git($"-C \"{_repo}\" symbolic-ref HEAD refs/heads/main"); // portable across git versions
        Git($"-C \"{_repo}\" config user.email t@t");
        Git($"-C \"{_repo}\" config user.name t");
        File.WriteAllText(_docsCsv, "epoch_seconds,account,bankroll\n");
        Git($"-C \"{_repo}\" add .");
        Git($"-C \"{_repo}\" commit -m init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Runs git and returns combined output; throws on failure so the
    /// fixture setup fails loudly.</summary>
    private static string Git(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        return output;
    }

    [Fact]
    public void ShouldAttemptPush_GatesOnMainAndDirty()
    {
        Assert.True(BankrollCsvPublisher.ShouldAttemptPush("main", " M docs/x\n"));
        Assert.False(BankrollCsvPublisher.ShouldAttemptPush("main", ""));
        Assert.False(BankrollCsvPublisher.ShouldAttemptPush("feature/x", " M docs/x\n"));
        Assert.False(BankrollCsvPublisher.ShouldAttemptPush(null, " M docs/x\n"));
    }

    [Fact]
    public void PushOnce_CommitsAndPushesOnlyTheCsv_FromMain()
    {
        // Local bare remote; the fixture's origin accepts fast-forward pushes.
        var remote = Path.Combine(_dir, "remote.git");
        Git($"init --bare \"{remote}\"");
        Git($"-C \"{_repo}\" remote add origin \"{remote}\"");
        Git($"-C \"{_repo}\" push -u origin main");

        File.WriteAllText(_docsCsv, "epoch_seconds,account,bankroll\n1,Alpha,0.90\n");
        var publisher = new BankrollCsvPublisher(_docsCsv);
        publisher.PushOnce();

        var status = Git($"-C \"{_repo}\" status --porcelain");
        Assert.Equal("", status.Trim()); // nothing left staged or dirty

        // The remote's committed CSV carries the refreshed content, and the
        // commit touched only that one path. (Explicit main, not HEAD: a bare
        // repo's HEAD can be an unborn default branch.)
        var remoteCsv = Git($"-C \"{remote}\" show main:docs/growth-bankroll.csv");
        Assert.Contains("1,Alpha,0.90", remoteCsv);
        var changed = Git($"-C \"{remote}\" show --name-only --format= main");
        Assert.Equal("docs/growth-bankroll.csv", changed.Trim());

        // The commit message marks it as the app's auto-publish.
        var msg = Git($"-C \"{remote}\" log -1 --format=%s main");
        Assert.Contains("Auto-refresh growth-bankroll export", msg);
    }

    [Fact]
    public void PushOnce_SkipsWhenCleanOrOffMain()
    {
        var remote = Path.Combine(_dir, "remote.git");
        Git($"init --bare \"{remote}\"");
        Git($"-C \"{_repo}\" remote add origin \"{remote}\"");
        Git($"-C \"{_repo}\" push -u origin main");
        var headBefore = Git($"-C \"{_repo}\" rev-parse HEAD").Trim();

        // Clean file → no commit.
        new BankrollCsvPublisher(_docsCsv).PushOnce();
        Assert.Equal(headBefore, Git($"-C \"{_repo}\" rev-parse HEAD").Trim());

        // Dirty file but on a feature branch → no commit.
        Git($"-C \"{_repo}\" checkout -b feature/x");
        File.WriteAllText(_docsCsv, "epoch_seconds,account,bankroll\n2,Beta,1.5\n");
        new BankrollCsvPublisher(_docsCsv).PushOnce();
        Assert.Equal(headBefore, Git($"-C \"{_repo}\" rev-parse HEAD").Trim());
        Assert.Contains("2,Beta", File.ReadAllText(_docsCsv)); // file left alone
    }

    [Fact]
    public void PushOnce_NeverThrows_OnBrokenRemote()
    {
        // origin points at a dead path: push fails, cycle must not throw and
        // the commit stays local for a retry after the remote is fixed.
        Git($"-C \"{_repo}\" remote add origin \"{Path.Combine(_dir, "missing.git")}\"");
        File.WriteAllText(_docsCsv, "epoch_seconds,account,bankroll\n3,G,2.0\n");

        new BankrollCsvPublisher(_docsCsv).PushOnce();

        var status = Git($"-C \"{_repo}\" status --porcelain");
        Assert.Equal("", status.Trim()); // committed locally, not lost
    }

    [Fact]
    public void PushOnce_NoOpsOutsideARepoCheckout()
    {
        // docsCsv exists but has no .git above it → constructor/Start accept
        // it, the cycle silently declines, nothing throws.
        var plain = Path.Combine(_dir, "no-repo", "growth-bankroll.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        File.WriteAllText(plain, "epoch_seconds,account,bankroll\n");
        new BankrollCsvPublisher(plain).PushOnce();
    }

    [Fact]
    public void Start_OutsideRepo_DoesNotThrow()
    {
        var plain = Path.Combine(_dir, "no-repo2", "growth-bankroll.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        using var publisher = new BankrollCsvPublisher(plain);
        publisher.Start(); // timer arms; ticks must also be exception-free
        publisher.PushOnce();
    }
}
