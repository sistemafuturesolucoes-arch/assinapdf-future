using Avalonia.Controls;

namespace AssinaPDF.MacPrototype;

public partial class MainWindow : Window
{
    private async void DashboardBatchSign_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var window = new BatchSignatureWindow();
            await window.ShowDialog(this);
            DashboardStatusText.Text = "Central de assinatura em lote fechada.";
        }
        catch (Exception ex)
        {
            DashboardStatusText.Text = "Não foi possível abrir a assinatura em lote: " + ex.Message;
        }
    }
}
