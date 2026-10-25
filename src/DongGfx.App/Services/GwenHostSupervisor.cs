using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace DongGfx.App.Services;

/// <summary>Keeps the desk's built-in GWEN host (DongGfx.GwenHost — the
/// llama.cpp model runner this app ships with itself) alive on loopback.
///
/// The isolation is the point: llama.cpp is native code, and native code
/// can fault. The model runs in a process we spawn and can kill, so a
/// model crash can never take the trading app down — the desk just sees
/// "no verdict" (fail-neutral) until the next health check succeeds and
/// the host is restarted, forever if need be, with capped backoff.
///
/// Lifecycle: started on demand (first assessment), watched through
/// /health, restarted on death, killed on app exit. If the exe or the
/// GGUF is missing, EnsureAsync returns false and the desk trades as if
/// GWEN did not exist — a missing model must never become a halt.</summary>
public sealed class GwenHostSupervisor : IDisposable
{
    public const int DefaultPort = 11435;

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly Func<bool> _healthProbe;
    private readonly Func<string, string, IHostProcess?> _starter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<TimeSpan> _sleep;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IHostProcess? _process;
    private int _consecutiveFailures;
    private DateTimeOffset _nextStartAt;
    private bool _disposed;

    /// <summary>Last start failure, for the operator/pill diagnostics.</summary>
    public string? LastError { get; private set; }

    public GwenHostSupervisor()
        : this(
            ProbeHealth,
            StartProcess,
            () => DateTimeOffset.UtcNow,
            ts => Thread.Sleep(ts))
    {
    }

    /// <summary>Test seam: injected probe/starter/clock — the same pattern
    /// the bridge tests use instead of reaching for the real machine.</summary>
    internal GwenHostSupervisor(
        Func<bool> healthProbe,
        Func<string, string, IHostProcess?> starter,
        Func<DateTimeOffset> clock,
        Action<TimeSpan> sleep)
    {
        _healthProbe = healthProbe;
        _starter = starter;
        _clock = clock;
        _sleep = sleep;
    }

    public Uri BaseUri => new($"http://127.0.0.1:{DefaultPort}/");

    /// <summary>Minimal process handle the supervisor needs (test seam).</summary>
    internal interface IHostProcess
    {
        bool HasExited { get; }
        void Kill();
    }

    private sealed class RealProcess : IHostProcess
    {
        private readonly Process _p;
        public RealProcess(Process p) => _p = p;
        public bool HasExited => _p.HasExited;
        public void Kill()
        {
            try { _p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>Best-effort: true when the host is (now) healthy. Never
    /// throws — a false here is a null verdict downstream.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (_disposed || _healthProbe())
        {
            return !_disposed;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || _healthProbe())
            {
                return !_disposed;
            }
            var exe = ResolveHostExe();
            var gguf = ResolveGguf();
            if (exe is null || gguf is null)
            {
                LastError = exe is null
                    ? "DongGfx.GwenHost.exe not found beside the app (or TF_GWEN_HOST_EXE)"
                    : "GWEN GGUF not found (TF_GWEN_GGUF or the desk's models dir)";
                return false;
            }

            var deadline = DateTimeOffset.UtcNow + ReadyDeadline;
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (_process is { HasExited: true })
                {
                    // Crash → count it and back off BEFORE respawning, so a
                    // host that dies on boot cannot burn a tight restart loop.
                    _process = null;
                    BumpBackoff();
                }
                if (_process is null)
                {
                    if (_clock() < _nextStartAt)
                    {
                        return false;   // in backoff — the desk keeps trading
                    }
                    var proc = _starter(exe, $"\"{gguf}\" --port {DefaultPort}");
                    if (proc is null)
                    {
                        LastError = "failed to start DongGfx.GwenHost";
                        return false;
                    }
                    _process = proc;
                }

                if (_healthProbe())
                {
                    _consecutiveFailures = 0;
                    LastError = null;
                    return true;
                }
                _sleep(TimeSpan.FromMilliseconds(500));
            }
            LastError = "host did not become healthy in time";
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void BumpBackoff()
    {
        _consecutiveFailures++;
        var backoff = TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(3, _consecutiveFailures - 1), MaxBackoff.TotalSeconds));
        _nextStartAt = _clock() + backoff;
    }

    private static bool ProbeHealth()
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthTimeout };
            using var resp = http.GetAsync(new Uri($"http://127.0.0.1:{DefaultPort}/health")).Result;
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static IHostProcess? StartProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            var p = Process.Start(psi);
            return p is null ? null : new RealProcess(p);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The host runner that ships with the app: beside the exe in a
    /// published build; the project's build output in a dev checkout.</summary>
    internal static string? ResolveHostExe()
    {
        // An explicit override is authoritative — even a missing file
        // there means "this env wants THAT exe", never a silent fallback
        // (tests and ops both rely on this determinism).
        var env = Environment.GetEnvironmentVariable("TF_GWEN_HOST_EXE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return File.Exists(env) ? env : null;
        }
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DongGfx.GwenHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DongGfx.GwenHost", "DongGfx.GwenHost.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The desk's GWEN weights. The Q4_K_M build is canonical on
    /// this 15 GB box (the f16 weights alone would page the desk into the
    /// swap file); f16 stays a fallback where RAM allows, and the legacy
    /// import location is kept last so an existing machine keeps working.</summary>
    internal static string? ResolveGguf()
    {
        var env = Environment.GetEnvironmentVariable("TF_GWEN_GGUF");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return File.Exists(env) ? env : null;
        }
        var candidates = new[]
        {
            @"E:\DonGFX\models\gwen-fx.Q4_K_M.gguf",
            @"E:\DonGFX\models\gwen-fx.f16.gguf",
            @"E:\ollama-models\blobs\sha256-1f08950590a395a944ef92a6f21024387348a8d75ed0c388b1cab1590762e95a",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _process?.Kill(); } catch { /* already gone */ }
        _gate.Dispose();
    }

    /// <summary>Interface so tests can fake the starter without exposing
    /// Process; kept nested to keep the surface small.</summary>
    internal static Func<string, string, IHostProcess?> StarterFactory => StartProcess;
}
