using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssinaPDF.MacPrototype;

public partial class EditorCenterWindow : Window
{
    private readonly EditorHistoryService _history = new();

    public EditorCenterWindow()
    {
        InitializeComponent();
        RefreshRecentDocuments();
    }

    private void RefreshRecentDocuments()
    {
        RecentDocumentsPanel.Children.Clear();
        var entries = _history.GetRecentEntries();

        if (entries.Count == 0)
        {
            RecentDocumentsPanel.Children.Add(new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(224, 227, 232)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Nenhum PDF editado ainda",
                            FontSize = 15,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.FromRgb(50, 55, 64))
                        },
                        new TextBlock
                        {
                            Text = "Clique em “Editar novo PDF” para começar. Seus trabalhos ficarão disponíveis aqui.",
                            FontSize = 12,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Foreground = new Avalonia.Media.SolidColorBrush(
                                Avalonia.Media.Color.FromRgb(112, 119, 130))
                        }
                    }
                }
            });
            return;
        }

        foreach (var entry in entries)
        {
            var details = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.OriginalFileName,
                        FontSize = 14,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Foreground = new Avalonia.Media.SolidColorBrush(
                            Avalonia.Media.Color.FromRgb(43, 48, 57)),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = $"Última edição: {entry.LastModifiedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}",
                        FontSize = 11,
                        Foreground = new Avalonia.Media.SolidColorBrush(
                            Avalonia.Media.Color.FromRgb(112, 119, 130))
                    },
                    new TextBlock
                    {
                        Text = entry.OriginalPath,
                        FontSize = 10,
                        Foreground = new Avalonia.Media.SolidColorBrush(
                            Avalonia.Media.Color.FromRgb(145, 150, 158)),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                    }
                }
            };

            var resumeButton = new Button
            {
                Content = "Retomar edição",
                Padding = new Avalonia.Thickness(14, 9),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(75, 82, 94)),
                Foreground = Avalonia.Media.Brushes.White,
                BorderThickness = new Avalonia.Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 132
            };
            resumeButton.Click += async (_, _) => await OpenEntryAsync(entry);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 16,
                Children = { details, resumeButton }
            };
            Grid.SetColumn(resumeButton, 1);

            RecentDocumentsPanel.Children.Add(new Border
            {
                Background = Avalonia.Media.Brushes.White,
                BorderBrush = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(224, 227, 232)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(16, 13),
                Child = row
            });
        }
    }

    private async void NewPdf_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Selecionar PDF para edição",
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

            var entry = _history.CreateWorkingCopy(files[0].Path.LocalPath);
            await OpenEntryAsync(entry);
        }
        catch (Exception ex)
        {
            CenterStatusText.Text = $"Não foi possível iniciar a edição: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task OpenEntryAsync(EditedPdfEntry entry)
    {
        if (!File.Exists(entry.WorkingCopyPath))
        {
            CenterStatusText.Text = "A cópia de trabalho não está mais disponível. Atualizando a lista...";
            RefreshRecentDocuments();
            return;
        }

        try
        {
            string documentDirectory = Path.GetDirectoryName(entry.WorkingCopyPath)
                ?? throw new InvalidOperationException("Não foi possível localizar a pasta do documento.");

            var editor = new EditorWindow(
                entry.WorkingCopyPath,
                entry.OriginalPath,
                documentDirectory);

            CenterStatusText.Text = $"Abrindo {entry.OriginalFileName}...";
            await editor.ShowDialog(this);
            _history.Touch(entry.Id);
            RefreshRecentDocuments();
            CenterStatusText.Text = "Documento salvo na sua lista de edições recentes.";
        }
        catch (Exception ex)
        {
            CenterStatusText.Text = $"Não foi possível abrir o editor: {ex.Message}";
        }
    }

    private void CloseCenter_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
