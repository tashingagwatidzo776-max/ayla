using System.IO;
using System.Threading;
using System.Windows;
using DongGfx.App.Infrastructure;
using DongGfx.App.Services;
using DongGfx.App.ViewModels;
using DongGfx.Core.Models;
using Xunit;

namespace DongGfx.App.Tests;

/// <summary>
/// SettingsViewModel's live webhook validation, mode label, quiet-save path
/// and busy guard; DashboardViewModel's risk-rail alerting, governor
/// states, kill-switch toggle, and push APIs (ReportFxStatus, SetSymbol,
/// SetBalance, AddHistory) — all without WPF dialogs or a webhook endpoint.
/// </summary>
[Trait("Category", "Unit")]
[Collection("Shared-DataDir-Directory")]
public class DashboardAndSettingsCoverageTests : IDisposable
{
    // SaveSettingsQuietAsync writes SettingsService.DataDir\settings.json -
    // resolve the file the same way the service does, so a TF_DATA_DIR
    // redirect (ci-local gate, dev loops) makes the test assert the scratch
    // copy it actually wrote, never the running app's real settings.
    private static readonly string SettingsFile = Path.Combine(
        SettingsService.DataDir, "settings.json");
    private readonly byte[]? _settingsBackup = File.Exists(SettingsFile) ? File.ReadAllBytes(SettingsFile) : null;

    public void Dispose()
    {
        try
        {
            if (_settingsBackup is not null) File.WriteAllBytes(SettingsFile, _settingsBackup);
            else File.Delete(SettingsFile);
        }
        catch { /* best effort */ }
    }

    // ── SettingsViewModel ─────────────────────────────────────────────

    // ── TP1 prototype arming (the confirm gate) ─────────────────────

    [Fact]
    public void Tp1_Arm_Requires_Confirmation_And_Fails_Closed()
    {
        var vm = new SettingsViewModel(new SettingsService());
        // No confirmation hook at all: fail-closed — the arm must not stick.
        vm.FxExecuteTp1Partials = true;
        Assert.False(vm.FxExecuteTp1Partials);
        Assert.Contains("not confirmed", vm.Tp1ToggleHint);
        Assert.False(FxEngineHost.ExecuteTp1Partials);

        // A declined confirmation behaves the same.
        var declined = new SettingsViewModel(new SettingsService()) { ConfirmTp1Arm = _ => false };
        declined.FxExecuteTp1Partials = true;
        Assert.False(declined.FxExecuteTp1Partials);
    }

    [Fact]
    public void Tp1_Arm_Applies_Live_And_Disarm_Clears_Immediately()
    {
        try
        {
            var vm = new SettingsViewModel(new SettingsService()) { ConfirmTp1Arm = _ => true };
            vm.FxExecuteTp1Partials = true;
            Assert.True(vm.FxExecuteTp1Partials);
            Assert.True(FxEngineHost.ExecuteTp1Partials, "arming applies without a restart");
            Assert.Contains("ARMED", vm.Tp1ToggleHint);

            // Disarm is unconditional and fail-safe.
            vm.FxExecuteTp1Partials = false;
            Assert.False(FxEngineHost.ExecuteTp1Partials);
            Assert.Contains("OFF", vm.Tp1ToggleHint);
        }
        finally
        {
            FxEngineHost.ExecuteTp1Partials = false;   // never leak into other tests
        }
    }

    [Fact]
    public void Load_Restores_Tp1_Armed_State_Without_Re_Prompting()
    {
        try
        {
            var prompted = 0;
            var vm = new SettingsViewModel(new SettingsService())
            {
                ConfirmTp1Arm = _ => { prompted++; return false; },
            };

            // Restoring a persisted arm is not a new operator act: the modal
            // must not re-appear on every launch (it used to block startup).
            vm.Load(new AppSettings { FxExecuteTp1Partials = true });

            Assert.Equal(0, prompted);
            Assert.True(vm.FxExecuteTp1Partials);          // state restored
            Assert.True(FxEngineHost.ExecuteTp1Partials);  // applied live
            Assert.Contains("ARMED", vm.Tp1ToggleHint);

            // A genuine toggle still prompts — fail-closed on decline.
            vm.FxExecuteTp1Partials = false;   // disarm: always allowed, no prompt
            Assert.Equal(0, prompted);
            vm.FxExecuteTp1Partials = true;    // arm: prompt fires
            Assert.Equal(1, prompted);
            Assert.False(vm.FxExecuteTp1Partials);         // declined → reverted
            Assert.False(FxEngineHost.ExecuteTp1Partials);
        }
        finally
        {
            FxEngineHost.ExecuteTp1Partials = false;   // never leak into other tests
        }
    }

    [Fact]
    public void Min_Entry_Confidence_Applies_Live_And_Clamps()
    {
        try
        {
            var vm = new SettingsViewModel(new SettingsService());

            // Applies without a restart — the live engine's floor follows
            // the editor the moment it moves.
            vm.FxMinEntryConfidence = 0.7;
            Assert.Equal(0.7, FxEngineHost.MinEntryConfidence);

            // A hand-typed value above 1 clamps: demanding confidence
            // > 1.0 would silently block every entry forever.
            vm.FxMinEntryConfidence = 5;
            Assert.Equal(1, FxEngineHost.MinEntryConfidence);

            // Off again → the historical behavior (every qualifying
            // signal dispatches).
            vm.FxMinEntryConfidence = 0;
            Assert.Equal(0, FxEngineHost.MinEntryConfidence);
        }
        finally
        {
            FxEngineHost.MinEntryConfidence = 0;   // never leak into other tests
        }
    }

    [Fact]
    public void Tp1_Hint_Shows_The_Overdue_Review_Breaker_Hold()
    {
        var dir = Path.Combine(SettingsService.DataDir, "watcher");
        var path = Path.Combine(dir, "tp1-plan-reviews.jsonl");
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new SettingsViewModel(new SettingsService()) { ConfirmTp1Arm = _ => true };
            vm.FxExecuteTp1Partials = true;

            // No review: plain ARMED.
            File.Delete(path);
            vm.RefreshTp1Hint();
            Assert.False(vm.Tp1BreakerHold);
            Assert.Contains("ARMED", vm.Tp1ToggleHint);
            Assert.DoesNotContain("HELD", vm.Tp1ToggleHint);

            // An overdue OPEN review: armed but HELD, with the reason.
            File.WriteAllText(path,
                "{\"event\": \"recommended\", \"id\": \"r1\", \"ts\": \""
                + DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
                + "\", \"direction\": \"down\"}\n");
            vm.RefreshTp1Hint();
            Assert.True(vm.Tp1BreakerHold);
            Assert.Contains("ARMED but HELD", vm.Tp1ToggleHint);
            Assert.Contains("overdue plan-% review", vm.Tp1ToggleHint);

            // Acted: the hold releases.
            File.AppendAllText(path,
                "{\"event\": \"acted\", \"id\": \"r1\", \"ts\": \""
                + DateTimeOffset.UtcNow.AddDays(-9).ToString("o") + "\"}\n");
            vm.RefreshTp1Hint();
            Assert.False(vm.Tp1BreakerHold);
            Assert.Contains("ARMED", vm.Tp1ToggleHint);
        }
        finally
        {
            try
            {
                if (backup is null) File.Delete(path);
                else File.WriteAllText(path, backup);
            }
            catch { /* best effort */ }
            FxEngineHost.ExecuteTp1Partials = false;
        }
    }

    [Fact]
    public void Tp1_Open_Review_Acts_In_App_And_Releases_The_Hold()
    {
        var dir = Path.Combine(SettingsService.DataDir, "watcher");
        var path = Path.Combine(dir, "tp1-plan-reviews.jsonl");
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new SettingsViewModel(new SettingsService()) { ConfirmTp1Arm = _ => true };
            vm.FxExecuteTp1Partials = true;
            File.WriteAllText(path,
                "{\"event\":\"recommended\",\"id\":\"r1\",\"ts\":\""
                + DateTimeOffset.UtcNow.AddDays(-10).ToString("o")
                + "\",\"direction\":\"down\",\"baseline_pct\":25,\"candidate_pct\":15}\n");
            vm.RefreshTp1Hint();

            Assert.True(vm.Tp1BreakerHold);
            Assert.Contains("⚠", vm.Tp1ToggleHint);            // warning glyph
            var row = Assert.Single(vm.OpenTp1Reviews);
            Assert.Equal("r1", row.Id);
            Assert.Contains("25%→15%", row.Label);

            // Acting in-app appends the acted event and releases the hold.
            vm.ActTp1ReviewCommand.Execute(row);
            Assert.False(vm.Tp1BreakerHold);
            Assert.DoesNotContain("HELD", vm.Tp1ToggleHint);
            Assert.Empty(vm.OpenTp1Reviews);
            Assert.Contains(File.ReadAllLines(path),
                l => l.Contains("\"event\":\"acted\""));
        }
        finally
        {
            try
            {
                if (backup is null) File.Delete(path);
                else File.WriteAllText(path, backup);
            }
            catch { /* best effort */ }
            FxEngineHost.ExecuteTp1Partials = false;
        }
    }

    [Fact]
    public void Tp1_Breaker_Hold_Surfaces_On_The_Dashboard_Banner()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ConfigureTp1PlanHold(null);
        Assert.False(dashboard.IsTp1PlanHeld);
        Assert.Equal("", dashboard.Tp1PlanHoldText);   // nothing shown

        dashboard.ConfigureTp1PlanHold(() => true);
        Assert.True(dashboard.IsTp1PlanHeld);
        Assert.Contains("TP1 rung HELD", dashboard.Tp1PlanHoldText);

        dashboard.ConfigureTp1PlanHold(() => false);
        Assert.False(dashboard.IsTp1PlanHeld);
        Assert.Equal("", dashboard.Tp1PlanHoldText);
    }

    [Fact]
    public void Tp1_Recommendation_Surfaces_On_The_Dashboard()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ConfigureTp1PlanReview(null);
        Assert.False(dashboard.HasTp1Recommendation);
        Assert.Equal("", dashboard.Tp1RecommendationText);

        var rec = new Tp1PlanReviewLedger.Recommendation(
            "r1", "down", 25, 15, -3.2, -1.07, 3, 3, 0,
            DateTimeOffset.UtcNow.AddDays(-10), 10, "open");
        dashboard.ConfigureTp1PlanReview(() => rec);

        Assert.True(dashboard.HasTp1Recommendation);
        Assert.Contains("TP1 plan-% recommendation", dashboard.Tp1RecommendationText);
        Assert.Contains("25% → 15%", dashboard.Tp1RecommendationText);
        Assert.Contains("3 graded", dashboard.Tp1RecommendationText);

        dashboard.ConfigureTp1PlanReview(() => null);
        Assert.False(dashboard.HasTp1Recommendation);
        Assert.Equal("", dashboard.Tp1RecommendationText);
        Assert.Equal("Arm recommendation", dashboard.Tp1RecommendationArmLabel);
    }

    [Fact]
    public void Tp1_Recommendation_Arms_And_Reverts_From_The_Dashboard()
    {
        // The one-click arm is the advice-to-action step: the button names the
        // plan % it will arm, the App layer persists the override the engine
        // honors, and the banner reflects the armed state — without a Settings
        // round-trip. This exercises the wiring with a stubbed App callback.
        var dashboard = new DashboardViewModel();
        var rec = new Tp1PlanReviewLedger.Recommendation(
            "r1", "down", 25, 15, -3.2, -1.07, 3, 3, 0,
            DateTimeOffset.UtcNow.AddDays(-10), 10, "open");
        dashboard.ConfigureTp1PlanReview(() => rec);

        // The button names the candidate it will arm, not a generic label.
        Assert.Equal("Arm 15%", dashboard.Tp1RecommendationArmLabel);
        Assert.False(dashboard.HasTp1PlanOverride);

        var armed = 0;
        var reverted = 0;
        dashboard.ArmTp1Plan = () =>
        {
            armed++;
            FxEngineHost.Tp1PlanPctOverride = 15;   // what the App layer persists
            return true;
        };
        dashboard.RevertTp1Plan = () =>
        {
            reverted++;
            FxEngineHost.Tp1PlanPctOverride = null;
            return true;
        };
        try
        {
            dashboard.ArmTp1RecommendationCommand.Execute(null);
            Assert.Equal(1, armed);
            Assert.True(dashboard.HasTp1PlanOverride);
            Assert.Contains("armed at 15%", dashboard.Tp1PlanOverrideText);

            dashboard.RevertTp1PlanOverrideCommand.Execute(null);
            Assert.Equal(1, reverted);
            Assert.False(dashboard.HasTp1PlanOverride);
            Assert.Equal("", dashboard.Tp1PlanOverrideText);

            // With no arm/revert hook (headless), the commands are inert but
            // never throw — the recommendation line still renders.
            var bare = new DashboardViewModel();
            bare.ConfigureTp1PlanReview(() => rec);
            bare.ArmTp1RecommendationCommand.Execute(null);
            bare.RevertTp1PlanOverrideCommand.Execute(null);
            Assert.False(bare.HasTp1PlanOverride);
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanReview(null);
        }
    }

    [Fact]
    public void Tp1_Override_Banner_Shows_The_Watchers_Verdict()
    {
        // The armed value alone says what the engine will do; the watcher's
        // verdict line says whether it has happened yet — and only while an
        // override is armed.
        var dashboard = new DashboardViewModel();
        try
        {
            FxEngineHost.Tp1PlanPctOverride = 15;

            // No reader wired (tests/headless): the armed value still shows,
            // the verdict line does not.
            dashboard.ConfigureTp1PlanOverrideCheck(null);
            Assert.True(dashboard.HasTp1PlanOverride);
            Assert.False(dashboard.HasTp1PlanOverrideCheck);
            Assert.Equal("", dashboard.Tp1PlanOverrideCheckText);

            var reads = 0;
            dashboard.ConfigureTp1PlanOverrideCheck(() =>
            {
                reads++;
                return new Tp1PlanOverrideCheck.Verdict(
                    "never-fired", 15, DateTimeOffset.UtcNow.AddDays(-3.2),
                    3.2, true, null, DateTimeOffset.UtcNow,
                    "engine-idle", "the brain loop is off");
            });
            Assert.Equal(1, reads);   // configure refreshes once
            Assert.True(dashboard.HasTp1PlanOverrideCheck);
            Assert.Contains("NEVER FIRED", dashboard.Tp1PlanOverrideCheckText);
            // The unmet precondition rides the same line, in parentheses.
            Assert.Contains("(the brain loop is off)",
                dashboard.Tp1PlanOverrideCheckText);
            Assert.Contains("armed at 15%", dashboard.Tp1PlanOverrideText);

            // Revert: nothing armed, so the verdict is not even fetched —
            // the banner collapses instead of reporting on a dead override.
            FxEngineHost.Tp1PlanPctOverride = null;
            dashboard.RefreshTp1PlanOverride();
            Assert.False(dashboard.HasTp1PlanOverride);
            Assert.False(dashboard.HasTp1PlanOverrideCheck);
            Assert.Equal(1, reads);   // not invoked while disarmed
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanOverrideCheck(null);
        }
    }

    [Fact]
    public void Tp1_Override_Action_Button_Maps_Each_Reason_To_Its_Fix()
    {
        // Every reason the banner can name gets the button that fixes it —
        // and only those: a reason the app cannot fix must offer nothing.
        var dashboard = new DashboardViewModel();
        try
        {
            FxEngineHost.Tp1PlanPctOverride = 15;

            void VerdictFor(string reason)
            {
                dashboard.ConfigureTp1PlanOverrideCheck(() =>
                    new Tp1PlanOverrideCheck.Verdict(
                        "waiting", 15, DateTimeOffset.UtcNow.AddHours(-2), 0.1,
                        false, null, DateTimeOffset.UtcNow,
                        reason, "because the precondition is unmet"));
            }

            VerdictFor("engine-idle");
            Assert.True(dashboard.HasTp1PlanAction);
            Assert.Equal("Start brain", dashboard.Tp1PlanActionLabel);

            VerdictFor("app-idle");
            Assert.Equal("Start brain", dashboard.Tp1PlanActionLabel);

            VerdictFor("partials-off");
            Assert.Equal("Arm TP1 partials", dashboard.Tp1PlanActionLabel);

            VerdictFor("hold-blocked");
            Assert.Equal("Act on review", dashboard.Tp1PlanActionLabel);

            // The trailing gate and a rung not yet eligible have no in-app
            // fix — offering a dead control would be worse than offering none.
            VerdictFor("gate-blocked");
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.Equal("", dashboard.Tp1PlanActionLabel);

            VerdictFor("no-eligible-rung");
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.Equal("", dashboard.Tp1PlanActionLabel);

            // Nothing armed: no verdict is fetched at all, so no button.
            FxEngineHost.Tp1PlanPctOverride = null;
            dashboard.RefreshTp1PlanOverride();
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.Equal("", dashboard.Tp1PlanActionLabel);
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanOverrideCheck(null);
        }
    }

    [Fact]
    public void Tp1_Override_Action_Dispatches_The_Fix_The_Button_Promised()
    {
        var dashboard = new DashboardViewModel();
        var started = 0;
        var armed = 0;
        var acted = 0;
        dashboard.StartTp1Brain = () => { started++; return true; };
        dashboard.ArmTp1Partials = () => { armed++; return true; };
        dashboard.ActTp1OverdueReview = () => { acted++; return true; };

        try
        {
            FxEngineHost.Tp1PlanPctOverride = 15;
            var reason = "engine-idle";
            dashboard.ConfigureTp1PlanOverrideCheck(() =>
                new Tp1PlanOverrideCheck.Verdict(
                    "waiting", 15, DateTimeOffset.UtcNow.AddHours(-2), 0.1,
                    false, null, DateTimeOffset.UtcNow, reason, "text"));

            // Each press runs exactly the hook its label named — no
            // cross-wiring between the fixes.
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Equal(1, started);
            Assert.Equal(0, armed);
            Assert.Equal(0, acted);

            reason = "partials-off";
            dashboard.RefreshTp1PlanOverride();
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Equal(1, started);
            Assert.Equal(1, armed);
            Assert.Equal(0, acted);

            reason = "hold-blocked";
            dashboard.RefreshTp1PlanOverride();
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Equal(1, acted);

            // A reason with no fix — and no reason at all — stays inert.
            reason = "no-eligible-rung";
            dashboard.RefreshTp1PlanOverride();
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            reason = "";
            dashboard.RefreshTp1PlanOverride();
            Assert.False(dashboard.HasTp1PlanAction);
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Equal(1, started);
            Assert.Equal(1, armed);
            Assert.Equal(1, acted);

            // Headless: a fixable reason with no hook wired never throws.
            var bare = new DashboardViewModel();
            bare.ConfigureTp1PlanOverrideCheck(() =>
                new Tp1PlanOverrideCheck.Verdict(
                    "waiting", 15, DateTimeOffset.UtcNow.AddHours(-2), 0.1,
                    false, null, DateTimeOffset.UtcNow, "engine-idle", "text"));
            bare.TakeTp1PlanActionCommand.Execute(null);
            Assert.True(bare.HasTp1PlanAction);   // the button still shows
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanOverrideCheck(null);
        }
    }

    [Fact]
    public void Dashboard_Soak_Line_Is_Inert_Without_A_Provider()
    {
        // Headless/bare construction: the line stays hidden and never throws.
        var bare = new DashboardViewModel();
        Assert.False(bare.HasFxSoakNote);
        Assert.Equal("", bare.FxSoakNote);
        bare.RefreshFxSoakNote();
        Assert.False(bare.HasFxSoakNote);

        // A provider with something to say shows it; clearing hides it.
        string? note = null;
        bare.ConfigureFxSoakNote(() => note);
        Assert.False(bare.HasFxSoakNote);

        note = "paper soak 13/40 · 4 carried over from the last session";
        bare.RefreshFxSoakNote();
        Assert.True(bare.HasFxSoakNote);
        Assert.Contains("carried over", bare.FxSoakNote);

        bare.ConfigureFxSoakNote(null);
        Assert.False(bare.HasFxSoakNote);
    }

    [Fact]
    public void Tp1_Override_Unfixable_Reason_Explains_The_Missing_Button()
    {
        // A blank where the button would be reads as a broken UI: the
        // reasons with no in-app fix must say so in its place.
        var dashboard = new DashboardViewModel();
        try
        {
            FxEngineHost.Tp1PlanPctOverride = 15;

            void VerdictFor(string reason) => dashboard.ConfigureTp1PlanOverrideCheck(() =>
                new Tp1PlanOverrideCheck.Verdict(
                    "waiting", 15, DateTimeOffset.UtcNow.AddHours(-2), 0.1,
                    false, null, DateTimeOffset.UtcNow, reason, "text"));

            VerdictFor("gate-blocked");
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.True(dashboard.HasTp1PlanActionHint);
            Assert.Contains("trailing gate", dashboard.Tp1PlanActionHint);

            VerdictFor("no-eligible-rung");
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.True(dashboard.HasTp1PlanActionHint);
            Assert.Contains("nothing is unmet", dashboard.Tp1PlanActionHint);

            // Fixable reasons show the button and never the stand-in text.
            VerdictFor("engine-idle");
            Assert.True(dashboard.HasTp1PlanAction);
            Assert.False(dashboard.HasTp1PlanActionHint);
            Assert.Equal("", dashboard.Tp1PlanActionHint);

            // Disarmed: neither the button nor the explanation.
            FxEngineHost.Tp1PlanPctOverride = null;
            dashboard.RefreshTp1PlanOverride();
            Assert.False(dashboard.HasTp1PlanAction);
            Assert.False(dashboard.HasTp1PlanActionHint);
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanOverrideCheck(null);
        }
    }

    [Fact]
    public void Tp1_Override_Action_Toasts_Whether_The_Fix_Took()
    {
        var notifier = new ToastCapture();
        var dashboard = new DashboardViewModel(notifier);
        try
        {
            FxEngineHost.Tp1PlanPctOverride = 15;
            var reason = "engine-idle";
            var started = true;
            dashboard.ConfigureTp1PlanOverrideCheck(() =>
                new Tp1PlanOverrideCheck.Verdict(
                    "waiting", 15, DateTimeOffset.UtcNow.AddHours(-2), 0.1,
                    false, null, DateTimeOffset.UtcNow, reason, "text"));
            dashboard.StartTp1Brain = () => started;

            // Success reports as success…
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            var ok = Assert.Single(notifier.Sent);
            Assert.Contains("Brain started", ok.Body);
            Assert.Equal("success", ok.Severity);

            // …and a refusal says so: a click with no feedback reads as a
            // dead button.
            started = false;
            notifier.Sent.Clear();
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            var failed = Assert.Single(notifier.Sent);
            Assert.Contains("did not start", failed.Body);
            Assert.Equal("warning", failed.Severity);

            // A declined partials arm reports the fail-closed outcome.
            notifier.Sent.Clear();
            reason = "partials-off";
            dashboard.RefreshTp1PlanOverride();
            dashboard.ArmTp1Partials = () => false;
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            var declined = Assert.Single(notifier.Sent);
            Assert.Contains("stays OFF", declined.Body);
            Assert.Equal("warning", declined.Severity);

            // Acting on the hold reports the release.
            notifier.Sent.Clear();
            reason = "hold-blocked";
            dashboard.RefreshTp1PlanOverride();
            dashboard.ActTp1OverdueReview = () => true;
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            var acted = Assert.Single(notifier.Sent);
            Assert.Contains("released", acted.Body);
            Assert.Equal("success", acted.Severity);

            // A reason with no fix dispatches nothing — and says nothing.
            notifier.Sent.Clear();
            reason = "gate-blocked";
            dashboard.RefreshTp1PlanOverride();
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Empty(notifier.Sent);

            // Toasts turned off: the action still runs, Windows stays
            // quiet — the setting must be honoured on this path too.
            notifier.Sent.Clear();
            reason = "engine-idle";
            dashboard.RefreshTp1PlanOverride();
            notifier.Enabled = false;
            dashboard.TakeTp1PlanActionCommand.Execute(null);
            Assert.Empty(notifier.Sent);
        }
        finally
        {
            FxEngineHost.Tp1PlanPctOverride = null;   // never leak into other tests
            dashboard.ConfigureTp1PlanOverrideCheck(null);
        }
    }

    /// <summary>Captures the outcome toast in-process — no PowerShell, no
    /// OS notification center involved.</summary>
    private sealed class ToastCapture : NotificationService
    {
        public List<(string Title, string Body, string Severity)> Sent { get; } = new();

        internal override void SendToast(string title, string body, string severity)
            => Sent.Add((title, body, severity));
    }

    [Fact]
    public void Tp1_History_Surfaces_On_The_Dashboard()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ConfigureTp1PlanHistory(null);
        Assert.False(dashboard.HasTp1ReviewHistory);

        var now = DateTimeOffset.UtcNow;
        dashboard.ConfigureTp1PlanHistory(() => new[]
        {
            new Tp1PlanReviewLedger.HistoryRow(
                "r2", "down", "↓ 35% → 25% · open 1d", "open", now.AddDays(-1)),
            new Tp1PlanReviewLedger.HistoryRow(
                "r1", "down", "↓ 25% → 15%", "acted", now.AddDays(-30)),
        });

        Assert.True(dashboard.HasTp1ReviewHistory);
        Assert.Equal(2, dashboard.Tp1ReviewHistory.Count);
        Assert.Equal("open", dashboard.Tp1ReviewHistory[0].Outcome);
        Assert.True(dashboard.Tp1ReviewHistory[0].IsOpen);
        Assert.Equal("✔ acted on", dashboard.Tp1ReviewHistory[1].Outcome);
    }

    [Fact]
    public void Tp1_Tunables_Editor_Loads_Validates_And_Saves()
    {
        // The editor writes the SHARED config file, so back it up and
        // restore it — exactly as the settings tests do for settings.json.
        var path = Tp1PlanGateConfig.ResolvedPath;
        Assert.NotNull(path);
        var backup = File.ReadAllText(path!);
        try
        {
            var vm = new SettingsViewModel(new SettingsService());
            vm.LoadTp1Tunables();
            Assert.Equal(Tp1PlanGateConfig.Keys.Count, vm.Tp1Tunables.Count);
            Assert.Contains(vm.Tp1Tunables,
                r => r.Key == "stale_days" && r.Value == "7");

            // An unparseable value is rejected, and nothing is written.
            var stale = vm.Tp1Tunables.First(r => r.Key == "stale_days");
            stale.Value = "not-a-number";
            vm.SaveTp1TunablesCommand.Execute(null);
            Assert.Contains("is not a number", vm.Tp1TunablesStatus);
            Assert.Equal(backup, File.ReadAllText(path!));   // fail-closed

            // An incoherent gate (floor above ceiling) is rejected too.
            stale.Value = "7";
            vm.Tp1Tunables.First(r => r.Key == "min_pct").Value = "90";
            vm.SaveTp1TunablesCommand.Execute(null);
            Assert.Contains("max_pct must exceed min_pct", vm.Tp1TunablesStatus);
            Assert.Equal(backup, File.ReadAllText(path!));

            // A valid edit writes the shared file and reloads the editor.
            vm.Tp1Tunables.First(r => r.Key == "min_pct").Value = "0";
            stale.Value = "3";
            vm.SaveTp1TunablesCommand.Execute(null);
            Assert.Contains("Saved", vm.Tp1TunablesStatus);
            Assert.Equal(3, Tp1PlanGateConfig.StaleDays);
            Assert.Equal("3",
                vm.Tp1Tunables.First(r => r.Key == "stale_days").Value);
        }
        finally
        {
            File.WriteAllText(path!, backup);
            Tp1PlanGateConfig.Reload();
            Tp1PlanBreaker.Invalidate();
        }
    }

    [Fact]
    public void Tp1_Tunables_Revert_Discards_Edits_And_Reloads_From_Disk()
    {
        var path = Tp1PlanGateConfig.ResolvedPath;
        Assert.NotNull(path);
        var backup = File.ReadAllText(path!);
        try
        {
            var vm = new SettingsViewModel(new SettingsService());
            vm.LoadTp1Tunables();
            var stale = vm.Tp1Tunables.First(r => r.Key == "stale_days");
            var original = stale.Value;

            stale.Value = "99";   // an unsaved edit
            vm.RevertTp1TunablesCommand.Execute(null);

            Assert.Equal("Tunables reverted to the shared config.", vm.StatusMessage);
            Assert.Equal(original, vm.Tp1Tunables.First(r => r.Key == "stale_days").Value);
            Assert.Equal(backup, File.ReadAllText(path!));   // never written
        }
        finally
        {
            File.WriteAllText(path!, backup);
            Tp1PlanGateConfig.Reload();
            Tp1PlanBreaker.Invalidate();
        }
    }

    [Fact]
    public async Task Settings_Pickers_And_Toggles_Load_And_Persist()
    {
        // The Settings tab's log-level and theme pickers and its two toggles
        // were exposed by the shell but unreferenced by any test.
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings
        {
            LogLevel = 2,
            Theme = ThemeManager.Classic,
            WebhookOnCircuitBreaker = false,
            FxLabEnabled = false,
        });

        Assert.Equal(new[] { "Debug", "Info", "Warn", "Error" }, vm.LogLevels);
        Assert.Equal(2, vm.LogLevel);
        Assert.Contains(ThemeManager.Dark, vm.Themes);
        Assert.Contains(ThemeManager.Classic, vm.Themes);
        Assert.Equal(ThemeManager.Classic, vm.Theme);
        Assert.False(vm.WebhookOnCircuitBreaker);
        Assert.False(vm.FxLabEnabled);

        // Flip them; the quiet save persists BuildSettings to disk.
        vm.LogLevel = 3;
        vm.FxLabEnabled = true;
        vm.WebhookOnCircuitBreaker = true;
        await vm.SaveSettingsQuietCommand.ExecuteAsync(null);

        var reloaded = new SettingsViewModel(new SettingsService());
        reloaded.Load(new SettingsService().Load());
        Assert.Equal(3, reloaded.LogLevel);
        Assert.True(reloaded.FxLabEnabled);
        Assert.True(reloaded.WebhookOnCircuitBreaker);
    }

    [Fact]
    public void Fault_Notice_Text_Reflects_The_Provider_And_Clears()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ConfigureFaultNotice(
            () => new DashboardViewModel.FaultNotice("fault: probe", SafeMode: true));

        Assert.Equal("fault: probe", dashboard.FaultNoticeText);
        Assert.True(dashboard.HasFaultNotice);
        Assert.True(dashboard.IsSafeMode);

        // A provider that throws must never become the next fault.
        dashboard.ConfigureFaultNotice(() => throw new InvalidOperationException("reader down"));
        Assert.Equal("", dashboard.FaultNoticeText);
        Assert.False(dashboard.HasFaultNotice);

        // Null clears it outright.
        dashboard.ConfigureFaultNotice(null);
        Assert.Equal("", dashboard.FaultNoticeText);
    }

    [Fact]
    public void Connection_Pill_Flag_Is_A_Notifying_Bool()
    {
        // The shell's status pill lights through a DataTrigger on
        // Dashboard.IsConnected; that surface is only reachable if the
        // property notifies.
        var dashboard = new DashboardViewModel();
        var changed = new System.Collections.Generic.List<string?>();
        dashboard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(dashboard.IsConnected);   // stays dark until reported in
        dashboard.IsConnected = true;

        Assert.True(dashboard.IsConnected);
        Assert.Contains("IsConnected", changed);
    }

    [Fact]
    public void ModeLabel_Follows_The_Demo_Flag()
    {
        var vm = new SettingsViewModel(new SettingsService());
        Assert.Equal("Demo", vm.ModeLabel);

        vm.IsDemo = false;
        Assert.Equal("Real", vm.ModeLabel);
    }

    [Fact]
    public void Webhook_Validation_Tracks_Url_And_Platform()
    {
        var vm = new SettingsViewModel(new SettingsService());
        Assert.Equal(WebhookUrlSeverity.None, vm.WebhookUrlSeverity);   // empty = optional

        vm.WebhookUrl = "not-a-url";
        Assert.Equal(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);
        Assert.Contains("valid", vm.WebhookValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.IsDiscordWebhook = true;   // platform change revalidates
        Assert.Equal(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);

        vm.WebhookUrl = "https://example.com/hook";
        Assert.NotEqual(WebhookUrlSeverity.Error, vm.WebhookUrlSeverity);
    }

    [Fact]
    public async Task QuietSave_Persists_And_Reports_Success()
    {
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings { IsDemo = true, FxSymbol = "XAUUSDmicro" });

        await vm.SaveSettingsQuietCommand.ExecuteAsync(null);

        Assert.Equal("Settings updated from the Terminal.", vm.StatusMessage);
        Assert.True(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Save_Command_Invokes_SaveAsync_And_Writes_Settings()
    {
        // The Settings tab's "Save settings" button binds SettingsVm.SaveCommand.
        // That command only exists because SaveAsync carries [RelayCommand] —
        // before it did, the binding resolved to nothing and the button was
        // inert. This drives the generated command so dropping the attribute
        // fails here instead of in the uia-smoke job.
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings { IsDemo = true });   // demo: no real-money dialog
        File.Delete(SettingsFile);                     // prove the command wrote it

        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(SettingsFile), "SaveCommand did not write settings.json");
        Assert.Contains("\"IsDemo\"", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task Real_Money_Save_Requires_The_Injected_Confirmation()
    {
        // Declined: nothing is written (fail-closed).
        var declined = new SettingsViewModel(new SettingsService())
        {
            IsDemo = false,
            ConfirmRealMoneySave = _ => false,
        };
        File.Delete(SettingsFile);
        await declined.SaveCommand.ExecuteAsync(null);
        Assert.Contains("cancelled", declined.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SettingsFile), "a declined real-money save wrote settings");

        // No hook at all is the headless/test default and must also refuse —
        // a missing dialog host can never mean "silently save real money".
        var headless = new SettingsViewModel(new SettingsService()) { IsDemo = false };
        await headless.SaveCommand.ExecuteAsync(null);
        Assert.Contains("cancelled", headless.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SettingsFile));

        // Confirmed: the save runs and persists.
        var confirmed = new SettingsViewModel(new SettingsService())
        {
            IsDemo = false,
            ConfirmRealMoneySave = _ => true,
        };
        await confirmed.SaveCommand.ExecuteAsync(null);
        Assert.Contains("REAL MONEY", confirmed.StatusMessage);
        Assert.True(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task Save_Busy_Guard_Skips_And_Happy_Path_Saves()
    {
        // Drives the XAML-bound command directly. IsDemo stays true so the
        // real-money MessageBox is never shown.
        var vm = new SettingsViewModel(new SettingsService());
        vm.Load(new AppSettings { IsDemo = true });

        vm.IsBusy = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Settings loaded.", vm.StatusMessage);   // guard returned early

        vm.IsBusy = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── DashboardViewModel: shadow-engine promotion card ──────────────

    private static string PromotionRow(long ticket, string engine, double exit, bool helped, string symbol = "EURUSD") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow,
            Ticket = ticket,
            Symbol = symbol,
            Engine = engine,
            ExitAtClose = exit,
            ResolvedAction = "full",
            Won = true,
            Helped = helped,
        });

    [Fact]
    public void PromotionCard_Rolls_Up_Ledgers_And_Speaks_The_Giveback_Line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-promo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "fx-shadow-EURUSD.jsonl"),
        [
            PromotionRow(1, "giveback", 0.95, helped: true),
            PromotionRow(2, "giveback", 0.85, helped: false),
        ]);
        File.WriteAllLines(Path.Combine(dir, "fx-shadow-XAUUSDmicro.jsonl"),
        [
            PromotionRow(3, "giveback", 0.9, helped: true, symbol: "XAUUSDmicro"),
            PromotionRow(4, "counterfactual", 0.7, helped: true, symbol: "XAUUSDmicro"),
        ]);

        var dashboard = new DashboardViewModel();
        dashboard.ConfigurePromotionLedger(dir);

        var giveback = dashboard.PromotionRows.First(r => r.Engine == "giveback");
        Assert.Equal("3 settled", giveback.Settled);
        Assert.Equal("2 helped", giveback.Evidence);
        Assert.Equal("hit 67%", giveback.HitRate);
        Assert.Contains("weight 0", giveback.Weight);

        // The headline line speaks the giveback engine's road to weight.
        Assert.Contains("giveback engine: 3 settled trade(s), 2 save(s), hit 67%", dashboard.PromotionSummaryText);
        Assert.Contains("weight 0 until 100 trades @ 60%", dashboard.PromotionSummaryText);
    }

    [Fact]
    public void PromotionCard_Degrades_To_The_Empty_State_Without_Ledgers()
    {
        var dashboard = new DashboardViewModel();
        dashboard.ConfigurePromotionLedger(null);

        Assert.Empty(dashboard.PromotionRows);
        Assert.Equal("no promotion evidence yet", dashboard.PromotionSummaryText);

        dashboard.ConfigurePromotionLedger(
            Path.Combine(Path.GetTempPath(), "dg-promo", Guid.NewGuid().ToString("N")));
        Assert.Empty(dashboard.PromotionRows);
        Assert.Equal("no promotion evidence yet", dashboard.PromotionSummaryText);
    }

    // ── DashboardViewModel ────────────────────────────────────────────

    [Fact]
    public void ReportFxStatus_Drives_Governor_Rails_And_Pnl_Text()
    {
        var dashboard = new DashboardViewModel();

        dashboard.ReportFxStatus("cycle ok", halted: false, warned: false, dayPnl: 12.5m);
        Assert.Equal("cycle ok", dashboard.FxStatusText);
        Assert.False(dashboard.IsGovernorLatched);
        Assert.False(dashboard.IsGovernorWarned);
        Assert.Equal("+$12.50", dashboard.CombinedGrowthPnlText);
        Assert.Empty(dashboard.RiskRailAlerts);

        dashboard.ReportFxStatus("warning zone", halted: false, warned: true, dayPnl: -3m);
        Assert.True(dashboard.IsGovernorWarned);
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("drawdown warning"));

        dashboard.ReportFxStatus("loss stop", halted: true, warned: true, dayPnl: null);
        Assert.True(dashboard.IsGovernorLatched);
        Assert.False(dashboard.IsGovernorWarned);   // warned yields to latched
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("loss stop latched"));
        Assert.DoesNotContain(dashboard.RiskRailAlerts, a => a.Contains("drawdown"));
    }

    [Fact]
    public void KillSwitch_Toggle_Engages_And_Releases_With_Status_Lines()
    {
        var dashboard = new DashboardViewModel();

        dashboard.ToggleKillSwitchCommand.Execute(null);
        Assert.True(dashboard.IsKillSwitchEngaged);
        Assert.Contains("KILL SWITCH ENGAGED", dashboard.StatusText);
        Assert.Contains(dashboard.RiskRailAlerts, a => a.Contains("kill switch"));

        dashboard.ToggleKillSwitchCommand.Execute(null);
        Assert.False(dashboard.IsKillSwitchEngaged);
        Assert.Contains("re-armed", dashboard.StatusText);
        Assert.DoesNotContain(dashboard.RiskRailAlerts, a => a.Contains("kill switch"));
    }

    [Fact]
    public void PushApis_Update_Texts_And_History_Safely()
    {
        var dashboard = new DashboardViewModel();

        dashboard.SetSymbol("XAUUSDmicro");
        Assert.Equal("XAUUSDmicro", dashboard.SymbolText);

        dashboard.SetBalance("$2,729.34");
        Assert.Equal("$2,729.34", dashboard.BalanceText);

        // Chart is null in tests — AddHistory must stay a safe no-op there
        // while still updating the readouts.
        var tick = new Tick("XAUUSDmicro", 2650.55, 2650.65, 2650.45,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 2);
        dashboard.AddHistory(new[] { tick });
        Assert.Equal("2650.55000", dashboard.LastPriceText);
        Assert.Equal("1 ticks", dashboard.TickCountText);

        dashboard.AddHistory(Array.Empty<Tick>());
        Assert.Equal("2650.55000", dashboard.LastPriceText);   // unchanged
    }

    [Fact]
    public void ThemeToggle_RoundTrips_And_Persists_BuildSettings()
    {
        var svc = new SettingsService();
        var vm = new SettingsViewModel(svc);
        vm.Load(svc.Load());
        Assert.Equal("Dark", vm.Theme);   // default on any machine

        vm.Theme = "Classic";
        Assert.Equal("Classic", vm.BuildSettings().Theme);
        svc.Save(vm.BuildSettings());

        var vm2 = new SettingsViewModel(svc);
        vm2.Load(svc.Load());
        Assert.Equal("Classic", vm2.Theme);   // round-trips through disk

        // Restore the machine default so other tests/users are unaffected.
        vm.Theme = "Dark";
        svc.Save(vm.BuildSettings());
    }

    [Fact]
    public void ThemeManager_Normalizes_Unknown_To_Dark()
    {
        Assert.Equal("Dark", ThemeManager.Normalize("dark"));
        Assert.Equal("Classic", ThemeManager.Normalize("CLASSIC"));
        Assert.Equal("Dark", ThemeManager.Normalize(""));
        Assert.Equal("Dark", ThemeManager.Normalize(null));
        Assert.Equal("Dark", ThemeManager.Normalize("neon-pink"));
    }

    [Fact]
    public void ThemeDictionaries_Expose_The_Same_Resource_Keys()
    {
        // Both themes must declare every key the app binds — a missing key
        // is a runtime XAML crash on switch, so this is a contract test.
        // Keys are parsed straight from the theme source files (pack URIs
        // don't resolve inside the test host).
        var dir = AppContext.BaseDirectory;
        string? root = null;
        var probe = dir;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (File.Exists(Path.Combine(probe, "DongGfx.sln")))
            {
                root = probe;
                break;
            }

            probe = Path.GetDirectoryName(probe);
        }

        if (root is null)
        {
            return;   // not running from a checkout (published artifacts) — skip
        }

        static System.Collections.Generic.HashSet<string> Keys(string path) =>
            System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(path), "x:Key=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

        var dark = Keys(Path.Combine(root, "src", "DongGfx.App", "Theme", "Dark.xaml"));
        var classic = Keys(Path.Combine(root, "src", "DongGfx.App", "Theme", "Classic.xaml"));

        Assert.NotEmpty(dark);
        Assert.Equal(dark, classic);
    }
}
