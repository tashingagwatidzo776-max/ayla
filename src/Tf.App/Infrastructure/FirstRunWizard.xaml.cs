using System.Windows;

namespace Tf.App.Infrastructure;

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
}

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

    public FirstRunWizard()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Completed = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ApiToken = TokenBox.Password;
        Symbol = SymbolBox.Text;
        Budget = FirstRunWizardLogic.ParseBudget(BudgetBox.Text);
        AutonomyEnabled = AutonomyCheck.IsChecked == true;
        RespectMarketHours = MarketHoursCheck.IsChecked == true;

        BrainKey = FirstRunWizardLogic.BrainKeyForIndex(BrainCombo.SelectedIndex);

        Completed = true;
        Close();
    }
}
