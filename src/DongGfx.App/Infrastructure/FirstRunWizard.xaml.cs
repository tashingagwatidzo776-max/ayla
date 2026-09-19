using System.Windows;
using DongGfx.Core.Models;

namespace DongGfx.App.Infrastructure;

/// <summary>
/// Pure mapping/validation logic extracted from the wizard's code-behind so
/// it is unit-testable headlessly (a WPF Window cannot be constructed in a
/// test). The code-behind calls these from its Save handler.
/// </summary>
public static class FirstRunWizardLogic
{
    /// <summary>Maps the brain combo's selected index to its registry key;
    /// anything out of range falls back to Growth.</summary>
    public static string BrainKeyForIndex(int selectedIndex) => selectedIndex switch
    {
        0 => "Growth",
        1 => "TrendFollowing",
        2 => "Breakout",
        3 => "MeanReversion",
        _ => "Growth"
    };

    /// <summary>Parses the budget box; unparsable/negative text falls back
    /// to the $5 default.</summary>
    public static decimal ParseBudget(string? text) =>
        decimal.TryParse(text, out var b) && b > 0 ? b : 5.00m;

    /// <summary>Whether the Save handler should accept the token field: a
    /// Deriv API token is required for any trading at all.</summary>
    public static bool IsTokenAcceptable(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= 8;

    /// <summary>Applies the wizard's choices to <paramref name="existing"/>
    /// by overwriting exactly the five wizard fields — every other setting
    /// (webhook, manual stake cap, governor/staleness caps, risk limits)
    /// survives. A completed wizard used to REPLACE the settings object
    /// wholesale, silently discarding configured rails; this merge closes
    /// that. IsDemo is forced true: the wizard always lands on a demo
    /// account — re-labelling to real stays the deliberate, gate-checked
    /// act it already was. Returns the same instance for convenience.</summary>
    public static AppSettings ApplyChoices(AppSettings? existing, FirstRunChoices choices)
    {
        var settings = existing ?? new AppSettings();
        settings.ApiToken = choices.ApiToken.Trim();
        settings.Symbol = string.IsNullOrWhiteSpace(choices.Symbol)
            ? AppSettings.DefaultSymbol
            : choices.Symbol.Trim();
        settings.AutonomyEnabled = choices.AutonomyEnabled;
        settings.RespectMarketHours = choices.RespectMarketHours;
        settings.IsDemo = true;
        return settings;
    }
}

/// <summary>The five fields the first-run wizard collects, ready to apply
/// via <see cref="FirstRunWizardLogic.ApplyChoices"/>. Exists so the merge
/// is testable headlessly (a WPF Window cannot be constructed in a test);
/// the code-behind builds it from its controls in the Save handler.</summary>
public sealed record FirstRunChoices(
    string ApiToken, string Symbol, string BrainKey,
    decimal Budget, bool AutonomyEnabled, bool RespectMarketHours);

/// <summary>
/// First-run setup wizard that guides new users through initial configuration.
/// Appears on the first launch and helps paste an API token, select a brain,
/// and configure basic settings.
/// </summary>
public partial class FirstRunWizard : Window
{
    public string ApiToken { get; set; } = "";
    public string Symbol { get; set; } = "frxEURUSD";
    public string BrainKey { get; set; } = "Growth";
    public decimal Budget { get; set; } = 5.00m;
    public bool AutonomyEnabled { get; set; }
    public bool RespectMarketHours { get; set; } = true;
    public bool Completed { get; private set; }

    /// <summary>True when the user chose "Skip for now": the app persists
    /// the completion flag for this outcome too, so a skipped setup reaches
    /// the Settings tab instead of the wizard re-appearing every launch.</summary>
    public bool Skipped { get; private set; }

    public FirstRunWizard()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Completed = false;
        Skipped = true;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ApiToken = TokenBox.Password;

        // Enforce the token floor before anything else: a wizard that
        // "completes" without a token strands the app configured but
        // token-less (the exact empty-config failure mode this wizard
        // exists to prevent). Stay open with an inline error instead.
        if (!FirstRunWizardLogic.IsTokenAcceptable(ApiToken))
        {
            TokenError.Text = "Enter a Deriv API token (at least 8 characters) — demo first.";
            TokenError.Visibility = Visibility.Visible;
            return;
        }
        TokenError.Visibility = Visibility.Collapsed;

        Symbol = SymbolBox.Text;
        Budget = FirstRunWizardLogic.ParseBudget(BudgetBox.Text);
        AutonomyEnabled = AutonomyCheck.IsChecked == true;
        RespectMarketHours = MarketHoursCheck.IsChecked == true;

        BrainKey = FirstRunWizardLogic.BrainKeyForIndex(BrainCombo.SelectedIndex);

        Completed = true;
        Close();
    }
}
