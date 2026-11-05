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
                Content = "Editar documento",
                Padding = new Avalonia.Thickness(14, 9),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(75, 82, 94)),
                Foreground = Avalonia.Media.Brushes.White,
                BorderThickness = new Avalonia.Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 132
            };
            resumeButton.Click += async (_, _) => await OpenEntryAsync(entry);

            var deleteButton = new Button
            {
                Content = "Excluir",
                Padding = new Avalonia.Thickness(14, 9),
                Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(250, 235, 235)),
                Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(164, 52, 52)),
                BorderThickness = new Avalonia.Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 82
            };
            deleteButton.Click += async (_, _) => await DeleteEntryAsync(entry);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                ColumnSpacing = 10,
                Children = { details, resumeButton, deleteButton }
            };
            Grid.SetColumn(resumeButton, 1);
            Grid.SetColumn(deleteButton, 2);

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
            // A central de edição abre diretamente o editor; a escolha do PDF
            // acontece pelo botão Abrir PDF na faixa de ferramentas.
            var editor = new EditorWindow();
            CenterStatusText.Text = "Editor iniciado. Use “Abrir PDF” para escolher um documento.";
            await editor.ShowDialog(this);
            RefreshRecentDocuments();
            CenterStatusText.Text = "Central de edição atualizada.";
        }
        catch (Exception ex)
        {
            CenterStatusText.Text = $"Não foi possível iniciar o editor: {ex.Message}";
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


    private async System.Threading.Tasks.Task DeleteEntryAsync(EditedPdfEntry entry)
    {
        bool confirmed = await ConfirmDeleteAsync(entry.OriginalFileName);
        if (!confirmed)
            return;

        try
        {
            _history.DeleteEntry(entry.Id);
            RefreshRecentDocuments();
            CenterStatusText.Text =
                $"“{entry.OriginalFileName}” e sua cópia de trabalho foram excluídos. O PDF original foi preservado.";
        }
        catch (Exception ex)
        {
            CenterStatusText.Text = $"Não foi possível excluir o documento: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDeleteAsync(string fileName)
    {
        var dialog = new Window
        {
            Title = "Confirmar exclusão",
            Width = 440,
            Height = 205,
            MinWidth = 440,
            MinHeight = 205,
            MaxWidth = 440,
            MaxHeight = 205,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(248, 249, 251))
        };

        var cancelButton = new Button
        {
            Content = "Cancelar",
            Padding = new Avalonia.Thickness(16, 8),
            MinWidth = 96
        };
        var deleteButton = new Button
        {
            Content = "Excluir documento",
            Padding = new Avalonia.Thickness(16, 8),
            MinWidth = 132,
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(164, 52, 52)),
            Foreground = Avalonia.Media.Brushes.White
        };

        cancelButton.Click += (_, _) => dialog.Close(false);
        deleteButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "Excluir este documento?",
                    FontSize = 18,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromRgb(41, 45, 54))
                },
                new TextBlock
                {
                    Text = $"“{fileName}” será removido do histórico e a cópia editada será apagada da pasta do AssinaPDF. O PDF original não será excluído.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromRgb(91, 98, 109))
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, deleteButton }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private void CloseCenter_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
