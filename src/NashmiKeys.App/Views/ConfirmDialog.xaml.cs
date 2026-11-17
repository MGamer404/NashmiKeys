using System.Windows;
using NashmiKeys.App.Helpers;

namespace NashmiKeys.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmLabel)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
