using System.Windows;

namespace Tf.App.Infrastructure;

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
        Budget = decimal.TryParse(BudgetBox.Text, out var b) ? b : 5.00m;
        AutonomyEnabled = AutonomyCheck.IsChecked == true;
        RespectMarketHours = MarketHoursCheck.IsChecked == true;

        BrainKey = BrainCombo.SelectedIndex switch
        {
            0 => "Growth",
            1 => "TrendFollowing",
            2 => "Breakout",
            3 => "MeanReversion",
            _ => "Growth"
        };

        Completed = true;
        Close();
    }
}
