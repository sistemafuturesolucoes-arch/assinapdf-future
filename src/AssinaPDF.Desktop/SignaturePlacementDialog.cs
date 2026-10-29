namespace FutureSolucoes.AssinaPDF;

internal sealed class SignaturePlacementDialog : Form
{
    private readonly NumericUpDown _pageNumber;
    private readonly NumericUpDown _xCoordinate;
    private readonly NumericUpDown _yCoordinate;
    private readonly TextBox _reason;

    public int PageNumber => (int)_pageNumber.Value;
    public float XCoordinate => (float)_xCoordinate.Value;
    public float YCoordinate => (float)_yCoordinate.Value;
    public string SigningReason => _reason.Text.Trim();

    public SignaturePlacementDialog()
    {
        Text = "Posição da assinatura visível";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 300);
        BackColor = Color.FromArgb(24, 24, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 6,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _pageNumber = CreateNumeric(1, 10000, 1);
        _xCoordinate = CreateNumeric(0, 10000, 50);
        _yCoordinate = CreateNumeric(0, 10000, 50);
        _reason = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 200,
            Text = "Assinatura digital",
            BackColor = Color.FromArgb(38, 38, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(3, 5, 3, 5)
        };

        AddRow(layout, 0, "Página do PDF:", _pageNumber);
        AddRow(layout, 1, "Posição X (pontos):", _xCoordinate);
        AddRow(layout, 2, "Posição Y (pontos):", _yCoordinate);
        AddRow(layout, 3, "Motivo:", _reason);

        var help = new Label
        {
            Dock = DockStyle.Fill,
            Text = "As coordenadas são medidas em pontos PDF a partir do canto inferior esquerdo da página. A posição padrão é 50 × 50. Confira o arquivo assinado antes de utilizá-lo.",
            ForeColor = Color.FromArgb(195, 195, 210),
            AutoSize = false,
            Padding = new Padding(2, 4, 2, 4)
        };
        layout.Controls.Add(help, 0, 4);
        layout.SetColumnSpan(help, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var okButton = CreateButton("Continuar");
        okButton.DialogResult = DialogResult.OK;
        var cancelButton = CreateButton("Cancelar");
        cancelButton.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private static NumericUpDown CreateNumeric(decimal minimum, decimal maximum, decimal value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        DecimalPlaces = 0,
        ThousandsSeparator = false,
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(38, 38, 48),
        ForeColor = Color.White,
        Margin = new Padding(3, 5, 3, 5)
    };

    private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control input)
    {
        layout.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gainsboro,
            Margin = new Padding(3)
        }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 5, 12, 5),
            BackColor = Color.FromArgb(124, 58, 237),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(6, 4, 0, 4)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
