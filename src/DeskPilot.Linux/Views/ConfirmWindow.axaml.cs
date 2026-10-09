using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskPilot.Core.Abstractions;
using DeskPilot.Linux.ViewModels;

namespace DeskPilot.Linux.Views;

/// <summary>Asks the user to approve one action. Closing it any other way than Allow means Deny.</summary>
public partial class ConfirmWindow : Window
{
    /// <summary>Designer only.</summary>
    public ConfirmWindow()
    {
        InitializeComponent();
        ViewModel = null!;
    }

    public ConfirmWindow(ProposedAction action)
    {
        InitializeComponent();
        ViewModel = new ConfirmViewModel(action);
        DataContext = ViewModel;
        Opened += (_, _) => DenyButton.Focus();
    }

    public ConfirmViewModel ViewModel { get; }

    public ConfirmationChoice Choice { get; private set; } = ConfirmationChoice.Deny;

    public void CloseWith(ConfirmationChoice choice)
    {
        Choice = choice;
        Close();
    }

    private void OnAllow(object? sender, RoutedEventArgs e) => CloseWith(ConfirmationChoice.Allow);

    private void OnAllowAll(object? sender, RoutedEventArgs e) => CloseWith(ConfirmationChoice.AllowAllThisTurn);

    private void OnDeny(object? sender, RoutedEventArgs e) => CloseWith(ConfirmationChoice.Deny);
}
