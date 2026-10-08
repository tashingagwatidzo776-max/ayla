using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Durable per-symbol paper-soak counters: how many alpha signals each
/// symbol has spoken while the brain ran in PAPER. The GO LIVE bar
/// (<c>PaperSoakComplete</c>, ten signals per symbol, all-or-nothing across
/// the portfolio) used to restart from zero on every launch — the counters
/// are in-memory while the journal keeps every signal, so a session that
/// closed at 9/10 on one symbol threw the whole portfolio's progress away
/// and the bar could only ever be met by one unbroken run.
///
/// Scope: the counters are stamped with the build identity, and a different
/// stamp DROPS them rather than inheriting them. The bar is evidence that
/// THIS build behaved in paper; silently carrying another build's 9/10 into
/// a changed engine would turn a proof into a formality. An unstamped dev
/// build shares one scope, so local rebuilds keep accruing — delete the
/// file (or <see cref="Reset"/>) to start over deliberately.
///
/// Account scope: the counters are ALSO stamped with the MT5 login they
/// were earned on, and counters earned on a different login are DROPPED the
/// same way. The bar is the evidence base for the REAL-MONEY go-live gate
/// on the account about to trade, so it must not carry across an account
/// switch — exactly like the real-money unlock, a freshly switched account
/// starts its own evidence. Stamping is optional (null account = no account
/// boundary, the behavior before this dimension existed) and only enforced
/// when both the stored and the current side name an account, so an older
/// ledger without one is never friendly-fired.
///
/// Read-modify-write, atomic (temp + move), best-effort: a lost write costs
/// one signal of progress, never a cycle. Never throws.
/// </summary>
public sealed class PaperSoakLedger
{
    /// <summary>The ledger's home beside the other FX state.</summary>
    public static string PathFor(string dataDir) =>
        Path.Combine(dataDir, "fx-paper-soak.json");

    private readonly string _path;
    private readonly string _scope;
    private readonly Func<string?>? _account;
    private readonly object _gate = new();
    private Dictionary<string, int>? _seen;

    /// <summary>The build stamp whose counters this load DROPPED (null when
    /// nothing was dropped) — the host journals it, so an invalidation is
    /// never silent.</summary>
    public string? InvalidatedScope { get; private set; }

    /// <summary>The MT5 login whose counters this load DROPPED because the
    /// account changed (null when nothing was dropped). The host journals
    /// it, so a switch that restarts the bar is never silent either.</summary>
    public string? InvalidatedAccount { get; private set; }

    /// <param name="path">Ledger file (see <see cref="PathFor"/>).</param>
    /// <param name="scope">Build identity the counters belong to.</param>
    /// <param name="account">Supplies the MT5 login the counters belong to,
    /// read LIVE each time the ledger touches disk (null disables the account
    /// boundary). A provider rather than a captured string so a switch made
    /// in the MT5 terminal's own GUI — not through the app's login dialog —
    /// still restarts the bar on the next brain start.</param>
    public PaperSoakLedger(string path, string scope, Func<string?>? account = null)
    {
        _path = path;
        _scope = scope;
        _account = account;
    }

    /// <summary>The live account, normalized (blank → null). A provider that
    /// throws must never break the soak: treat it as unknown.</summary>
    private string? CurrentAccount()
    {
        try
        {
            var value = _account?.Invoke();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persisted signals for a symbol (0 when unknown).</summary>
    public int Seen(string symbol) =>
        Snapshot().TryGetValue(symbol, out var n) ? n : 0;

    /// <summary>Counts one freshly observed paper signal and persists the
    /// result. Monotonic: the caller passes what it already counted in
    /// memory and the higher of the two wins, so a write that failed (or a
    /// restore that raced one) can only ever understate the soak — never
    /// invent progress toward a live flip.</summary>
    public int Record(string symbol, int atLeast)
    {
        lock (_gate)
        {
            var seen = LoadLocked();
            var next = Math.Max(atLeast,
                seen.TryGetValue(symbol, out var n) ? n : 0) + 1;
            seen[symbol] = next;
            SaveLocked(seen);
            return next;
        }
    }

    /// <summary>Clears every counter (deliberate restart of the soak, or a
    /// test's clean slate).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            SaveLocked(_seen);
        }
    }

    /// <summary>A copy, so a caller can never mutate the cache.</summary>
    private Dictionary<string, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, int>(LoadLocked(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, int> LoadLocked()
    {
        if (_seen is not null)
        {
            return _seen;
        }

        var fresh = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _seen = fresh;
        try
        {
            if (!File.Exists(_path))
            {
                return fresh;   // cold start
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return fresh;
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("symbols", out var symbols)
                && symbols.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in symbols.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number
                        && p.Value.TryGetInt32(out var n) && n > 0)
                    {
                        counts[p.Name] = n;
                    }
                }
            }

            var scope = root.TryGetProperty("scope", out var s)
                && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            if (!string.Equals(scope, _scope, StringComparison.Ordinal))
            {
                // Another build's evidence: drop it, and name it so the
                // drop is journaled rather than passed off as a cold start.
                if (counts.Count > 0)
                {
                    InvalidatedScope = scope.Length > 0 ? scope : "(unstamped)";
                }
                return fresh;
            }

            // Account boundary: evidence earned on another login does not
            // transfer to the account about to trade. Only enforced when
            // both sides name an account, so a ledger written before this
            // dimension existed keeps accruing rather than friendly-firing.
            var storedAccount = root.TryGetProperty("account", out var accEl)
                && accEl.ValueKind == JsonValueKind.String
                ? accEl.GetString() ?? "" : "";
            if (CurrentAccount() is { Length: > 0 } currentAccount
                && storedAccount.Length > 0
                && !string.Equals(storedAccount, currentAccount, StringComparison.OrdinalIgnoreCase))
            {
                if (counts.Count > 0)
                {
                    InvalidatedAccount = storedAccount;
                }
                return fresh;
            }

            foreach (var kv in counts)
            {
                fresh[kv.Key] = kv.Value;
            }
            return fresh;
        }
        catch
        {
            // Missing / unreadable / malformed: cold start, never a crash.
            fresh.Clear();
            return fresh;
        }
    }

    private void SaveLocked(Dictionary<string, int> seen)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = JsonSerializer.Serialize(new
            {
                scope = _scope,
                account = CurrentAccount(),
                updated_at = DateTimeOffset.UtcNow.ToString("o"),
                symbols = seen.OrderBy(p => p.Key, StringComparer.Ordinal)
                              .ToDictionary(p => p.Key, p => p.Value),
            }, new JsonSerializerOptions { WriteIndented = true });

            // Write-then-move: the app reads this file while another host
            // (or another session) may be writing it, and a half-written
            // ledger would read as zero progress.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort: a lost write costs one signal of progress.
        }
    }
}
