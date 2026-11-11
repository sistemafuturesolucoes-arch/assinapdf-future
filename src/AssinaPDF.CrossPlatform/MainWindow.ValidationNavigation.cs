using System;
using Avalonia.Controls;

namespace AssinaPDF.MacPrototype;

public partial class MainWindow : Window
{
    private async void OpenValidationCenter_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var validationWindow = new ValidationWindow();
            await validationWindow.ShowDialog(this);
            DashboardStatusText.Text = "Central de validação encerrada.";
        }
        catch (Exception ex)
        {
            DashboardStatusText.Text = $"Não foi possível abrir a central de validação: {ex.Message}";
        }
    }
}
