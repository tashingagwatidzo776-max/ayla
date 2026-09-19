using DongGfx.Core.Models;

namespace DongGfx.Core.Brain;

/// <summary>
/// Runs one full brain cycle: build the market context packet, obtain a
/// structured decision (from the LLM or a deterministic brain), validate it
/// with the risk engine, and (when authorized) place and settle the trade.
/// The autonomous scheduler drives this on a timer; the UI can invoke it
/// manually.
/// </summary>
public sealed class TradingBrain
{
    /// <summary>Produces a decision for a cycle. Rules brains ignore lessons.</summary>
    public delegate Task<BrainDecision> Decide(
        MarketContext context, RiskContext risk, IReadOnlyList<string> lessons, CancellationToken ct);

    /// <summary>
    /// Minimal broker surface the brain needs; the WPF shell wires this to
    /// the live DerivClient.
    /// </summary>
    public sealed class DerivAbstraction
    {
        public required Func<string, Direction, decimal, string, int, CancellationToken, Task<Proposal>> GetProposal { get; init; }
        public required Func<string, double, CancellationToken, Task<BuyResult>> Buy { get; init; }
        public required Func<string, TimeSpan?, CancellationToken, Task<ContractInfo>> WaitForSettlement { get; init; }
    }

    private readonly Decide _decide;
    private readonly Func<AppSettings> _settings;
    private readonly DerivAbstraction _deriv;

    /// <summary>LLM-driven brain (the original decision engine).</summary>
    public TradingBrain(LlmClient llm, Func<AppSettings> settings, DerivAbstraction deriv)
        : this((context, _, lessons, ct) => DecideWithLlmAsync(llm, context, lessons, ct), settings, deriv)
    {
    }

    /// <summary>Brain driven by any decision source (LLM, rules, growth engine…).</summary>
    public TradingBrain(Decide decide, Func<AppSettings> settings, DerivAbstraction deriv)
    {
        _decide = decide ?? throw new ArgumentNullException(nameof(decide));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _deriv = deriv ?? throw new ArgumentNullException(nameof(deriv));
    }

    /// <summary>
    /// Full cycle: build context, decide, risk-check, and — when allowed and
    /// the caller enables trading — place the trade. A settled trade is
    /// surfaced on <see cref="BrainCycleResult.ExecutedTrade"/> so the shell
    /// can tag and persist it per account.
    /// </summary>
    public async Task<BrainCycleResult> RunCycleAsync(
        IReadOnlyList<Tick> window, RiskContext risk, IReadOnlyList<string> lessons,
        bool allowTrading, CancellationToken ct = default)
    {
        var settings = _settings();
        var context = MarketContextBuilder.Build(window, risk.Balance, settings.Currency, lessons);
        var decided = await _decide(context, risk, lessons, ct).ConfigureAwait(false);
        var decision = decided.Decision;
        var verdict = new RiskEngine(settings).Evaluate(decision, risk);

        Trade? executed = null;
        if (allowTrading && verdict.Allowed && decision.Direction != BrainDirection.Hold)
        {
            executed = await PlaceAndSettleAsync(decision, settings, ct).ConfigureAwait(false);
        }

        return new BrainCycleResult(context, decision, verdict, decided.Raw, executed);
    }

    private async Task<Trade> PlaceAndSettleAsync(LlmDecision decision, AppSettings settings, CancellationToken ct)
    {
        var direction = decision.Direction == BrainDirection.Rise ? Direction.Rise : Direction.Fall;
        var proposal = await _deriv.GetProposal(
            settings.Symbol, direction, decision.Stake, settings.Currency,
            settings.DurationMinutes, ct).ConfigureAwait(false);
        var buy = await _deriv.Buy(proposal.Id, proposal.Spot, ct).ConfigureAwait(false);
        var final = await _deriv.WaitForSettlement(
            buy.ContractId, TimeSpan.FromMinutes(settings.DurationMinutes + 3), ct).ConfigureAwait(false);

        return new Trade(
            Guid.NewGuid(), settings.Symbol, direction, decision.Stake, settings.Currency,
            final.EntrySpot, final.EntryTime, final.ContractId, final.Status, final.Profit,
            final.ExitSpot > 0 ? final.ExitSpot : null,
            final.ExitTime > 0 ? final.ExitTime : null,
            DateTimeOffset.UtcNow);
    }

    private static async Task<BrainDecision> DecideWithLlmAsync(
        LlmClient llm, MarketContext context, IReadOnlyList<string> lessons, CancellationToken ct)
    {
        var prompt = MarketContextBuilder.ToPromptJson(context);
        try
        {
            var raw = await llm.CompleteJsonAsync(MarketContextBuilder.SystemPrompt, prompt, ct).ConfigureAwait(false);
            return new BrainDecision(DecisionParser.Parse(raw), raw);
        }
        catch (LlmException ex)
        {
            var message = $"LLM error: {ex.Message}";
            return new BrainDecision(LlmDecision.Hold(message), message);
        }
    }
}
