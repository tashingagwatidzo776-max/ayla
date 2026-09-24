using System.Windows;

namespace DongGfx.App;

/// <summary>MT5 account login dialog (File→Login). UI shell only — all
/// logic lives in <see cref="ViewModels.LoginViewModel"/> so it stays
/// unit-testable without a window.</summary>
public partial class LoginDialog : Window
{
    public LoginDialog()
    {
        InitializeComponent();
    }
}
