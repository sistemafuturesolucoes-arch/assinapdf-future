using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssinaPDF.MacPrototype;

public partial class MainWindow : Window
{
    private async void DashboardEditNew_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Selecionar PDF para editar",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Documentos PDF")
                        {
                            Patterns = new[] { "*.pdf", "*.PDF" }
                        }
                    }
                });

            if (files.Count == 0)
                return;

            string originalPath = Path.GetFullPath(files[0].Path.LocalPath);
            string sessionDirectory = Path.Combine(
                Path.GetTempPath(),
                "FutureSolucoes",
                "AssinaPDF",
                "EditorSessions",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(sessionDirectory);
            string workingCopyPath = Path.Combine(
                sessionDirectory,
                Path.GetFileName(originalPath));

            try
            {
                // O editor recebe somente uma cópia temporária.
                // O arquivo escolhido pelo usuário nunca é aberto para gravação.
                File.Copy(originalPath, workingCopyPath, overwrite: false);

                var editor = new EditorWindow(
                    workingCopyPath,
                    originalPath,
                    sessionDirectory);

                editor.Show(this);
                DashboardStatusText.Text =
                    $"Editor aberto com uma cópia de: {Path.GetFileName(originalPath)}";
            }
            catch
            {
                try
                {
                    if (Directory.Exists(sessionDirectory))
                        Directory.Delete(sessionDirectory, recursive: true);
                }
                catch
                {
                    // A limpeza da sessão é best-effort.
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            DashboardStatusText.Text = $"Não foi possível abrir o editor: {ex.Message}";
        }
    }
}