using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DongGfx.Core.Analytics;
using DongGfx.Core.Logging;
using DongGfx.Core.Models;

namespace DongGfx.App.Services;

/// <summary>
/// Turns MT5 deal history into performance data. The bridge's <c>/deals</c>
/// endpoint is the FX side's settlement feed: every closed deal carries a
/// realised P/L, which this feed maps onto the venue-neutral
/// <see cref="Trade"/> record and pushes into <see cref="PerformanceTracker"/>
/// (Performance tab, metrics exports) and <see cref="TradeJournal"/>
/// (Journal tab). Deals are deduplicated by ticket, so polling is idempotent.
/// </summary>
/// <remarks>
/// The terminal may be down or the sidecar unstarted: every refresh then
/// simply reports zero new deals and the next tick retries. Nothing here can
/// place, modify or cancel an order — it only reads history.
/// </remarks>
public sealed class FxTradeFeed : IDisposable
{
    private readonly Mt5BridgeClient _mt5;
    private readonly PerformanceTracker _tracker;
    private readonly TradeJournal _journal;
    private readonly Func<IReadOnlyList<Mt5Deal>> _dealsProvider;
    private readonly HashSet<long> _seenTickets = new();
    private readonly object _sync = new();
    private readonly List<Trade> _trades = new();
    private System.Threading.Timer? _timer;
    private string? _accountName;
    private Guid? _accountId;
    private decimal _bankroll;
    private bool _firstFxSettlementAnnounced;

    /// <summary>Optional sink for milestone notifications (Discord/Slack).
    /// Kept as a delegate so this read-only history feed stays decoupled
    /// from the webhook service and trivially fakeable in tests.</summary>
    public Action<string, string, bool>? MilestoneNotifier { get; set; }

    /// <summary>Whether milestone posts are enabled (the persisted
    /// WebhookOnMilestone toggle). Null/true = enabled; a false-answering
    /// provider silences milestone posts without touching settlements.</summary>
    public Func<bool>? MilestonesEnabled { get; set; }

    public FxTradeFeed(
        Mt5BridgeClient mt5,
        PerformanceTracker tracker,
        TradeJournal journal,
        Func<IReadOnlyList<Mt5Deal>>? dealsProvider = null)
    {
        _mt5 = mt5;
        _tracker = tracker;
        _journal = journal;
        _dealsProvider = dealsProvider ?? (() => Array.Empty<Mt5Deal>());
    }

    /// <summary>Realised FX trades seen this session, newest first.</summary>
    public IReadOnlyList<Trade> Trades
    {
        get
        {
            lock (_sync)
            {
                return _trades.OrderByDescending(t => t.SettledAt).ToArray();
            }
        }
    }

    /// <summary>Start the periodic refresh (first pass after 15 s, then every
    /// 2 minutes). Headless: the timer callback marshals its own errors.</summary>
    public void Start(TimeSpan? period = null) =>
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null,
            TimeSpan.FromSeconds(15), period ?? TimeSpan.FromMinutes(2));

    /// <summary>Poll the bridge once; returns how many new deals were
    /// recorded. Safe to call directly from tests.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var deals = await _mt5.GetDealsAsync(days: 7, ct).ConfigureAwait(false);
        if (deals.Count == 0)
        {
            // Also honour a provider injected by tests (no bridge involved).
            deals = _dealsProvider();
        }

        if (deals.Count == 0)
        {
            return 0;
        }

        await EnsureAccountAsync(ct).ConfigureAwait(false);

        var added = 0;
        foreach (var deal in deals)
        {
            // Only closing legs carry a realised outcome; entry legs (flat
            // 0 P/L) are remembered so a re-poll never reprocesses them.
            var net = (decimal)(deal.Profit + deal.Commission + deal.Swap);
            lock (_sync)
            {
                if (!_seenTickets.Add(deal.Ticket))
                {
                    continue;
                }

                if (net == 0)
                {
                    continue;
                }
            }

            // Notional at close stands in for "stake" so ROI in the metrics
            // export reads as return on exposure.
            var stake = (decimal)(deal.Volume * deal.Price);
            var trade = new Trade(
                Id: Guid.NewGuid(),
                Symbol: deal.Symbol,
                Stake: stake,
                Profit: net,
                SettledAt: DateTimeOffset.FromUnixTimeSeconds(deal.Time),
                Source: TradeSource.Fx,
                AccountId: _accountId,
                AccountName: _accountName);

            lock (_sync)
            {
                _trades.Add(trade);
                _bankroll += net;
            }

            // First settled demo trade = the go/no-go evidence milestone: it
            // is what unblocks the bankroll drill (#44) and what turns the
            // soak reports' verdicts from "nothing settled" into real
            // outcomes. Only a FRESH settlement announces: the feed re-ingests
            // the venue's whole 7-day deal window on every startup, and a
            // week-old historical import must not masquerade as the first
            // trade of the go-live era (it must not POISON the gate either —
            // hence an explicit MILESTONE journal marker as the once-ever
            // record, not a bare "a settlement exists somewhere" check).
            if (!_firstFxSettlementAnnounced
                && (MilestonesEnabled?.Invoke() ?? true)
                && IsFreshSettlement(deal)
                && !IsTicketJournaled(deal.Ticket)
                && !FirstSettlementAlreadyAnnounced())
            {
                _firstFxSettlementAnnounced = true;
                _journal.Log(Guid.Empty, "MILESTONE",
                    $"first settled FX trade announced (ticket {deal.Ticket}) — " +
                    "bankroll drill (issue #44) unblocked", "{}");
                MilestoneNotifier?.Invoke(
                    "🥇 First settled FX trade",
                    $"{trade.Symbol} {deal.Side} settled {trade.Profit:+0.##;-0.##;0} " +
                    $"(ticket {deal.Ticket}, {_accountName ?? "MT5 demo"}) — " +
                    "the bankroll drill (issue #44) is unblocked.",
                    true);
            }

            _tracker.RecordTrade(trade);
            _journal.LogTradeSettlement(
                _accountId ?? Guid.Empty,
                deal.Ticket.ToString(),
                trade.IsWin,
                stake + net,   // gross return (payout)
                net,
                _bankroll);     // running FX P/L stands in for bankroll
            added++;
        }

        _tracker.Save();
        return added;
    }

    /// <summary>How old, at ingest, a deal must be to count as a live
    /// settlement rather than a startup import of the venue's history.
    /// The feed polls every 2 minutes; 15 minutes is generous slack for a
    /// poll delay plus clock skew while still excluding every deal a
    /// restart re-imports.</summary>
    internal static readonly TimeSpan FreshSettlementWindow = TimeSpan.FromMinutes(15);

    /// <summary>True when the deal closed within the fresh window — i.e. it
    /// was settled by the market moments ago, not imported from history.</summary>
    private static bool IsFreshSettlement(Mt5Deal deal) =>
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(deal.Time) <= FreshSettlementWindow;

    /// <summary>True when this deal's ticket already sits in the journal as
    /// a TRADE_SETTLEMENT (a restart's re-ingest of an already-recorded
    /// settle). False on any read failure — fail toward announcing.</summary>
    private bool IsTicketJournaled(long ticket)
    {
        try
        {
            var needle = $"\"ContractId\":\"{ticket}\"";
            return _journal.GetRecent(count: 10000)
                .Any(e => e.Category == "TRADE_SETTLEMENT" && e.Details.Contains(needle, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True once the first-settlement milestone has EVER fired —
    /// the MILESTONE journal entry written at announce time is the once-per-
    /// installation record. History imports never write it, so they can
    /// neither trigger nor suppress the genuine first live settlement.</summary>
    private bool FirstSettlementAlreadyAnnounced()
    {
        try
        {
            return _journal.GetRecent(count: 10000)
                .Any(e => e.Category == "MILESTONE" && e.Details.Contains("first settled FX trade"));
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureAccountAsync(CancellationToken ct)
    {
        if (_accountName is not null)
        {
            return;
        }

        var account = await _mt5.GetAccountAsync(ct).ConfigureAwait(false);
        if (account is null)
        {
            return;
        }

        _accountName = $"MT5 {account.Login} ({account.Server})";
        _accountId = GuidFrom(account.Login.ToString());
    }

    /// <summary>Deterministic per-account id so the same MT5 login always
    /// aggregates into one row (and survives restarts).</summary>
    private static Guid GuidFrom(string key)
    {
        var hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public void Dispose() => _timer?.Dispose();
}
