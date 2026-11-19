using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public sealed class AccountWindow : Window
{
    private readonly SupabaseAuthService _auth;
    private readonly TextBox _name = new() { PlaceholderText = "Seu nome completo" };
    private readonly TextBox _email = new() { PlaceholderText = "E-mail" };
    private readonly TextBox _password = new() { PlaceholderText = "Senha (mínimo 6 caracteres)", PasswordChar = '•' };
    private readonly TextBox _activationKey = new() { PlaceholderText = "FUT-XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5E6470") };
    private readonly TextBlock _account = new() { Foreground = Brush.Parse("#454B55"), FontWeight = FontWeight.SemiBold };
    private readonly Button _loginTab;
    private readonly Button _registerTab;
    private readonly Button _submitButton;
    private readonly Button _signOutButton;
    private bool _registerMode;
    private bool _signedIn;

    public AccountWindow(SupabaseAuthService auth)
    {
        _auth = auth;
        Title = "Minha conta | AssinaPDF";
        Width = 610;
        Height = 760;
        MinWidth = 500;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#F3F4F6");
        CanResize = true;

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(26) };
        root.Children.Add(new TextBlock
        {
            Text = "Minha conta AssinaPDF", FontSize = 25, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Entre na sua conta ou cadastre-se para ativar e acompanhar seu plano. Sua senha e seus certificados permanecem no seu computador.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#737B87"), FontSize = 13
        });

        _loginTab = TabButton("Entrar", false);
        _registerTab = TabButton("Cadastrar", true);
        _loginTab.Click += (_, _) => SetRegisterMode(false);
        _registerTab.Click += (_, _) => SetRegisterMode(true);
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        Grid.SetColumn(_registerTab, 1);
        tabs.Children.Add(_loginTab);
        tabs.Children.Add(_registerTab);

        _submitButton = RowButton("Entrar", SignIn_Click, "#6741B8");
        _signOutButton = RowButton("Sair da conta", SignOut_Click, "#737B87");
        root.Children.Add(Card("Acesso à sua conta", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                tabs,
                _name,
                _email,
                _password,
                _submitButton,
                _account,
                _signOutButton
            }
        }));

        root.Children.Add(Card("Ativar sua chave de acesso", new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock
                {
                    Text = "Já tem sua chave de acesso? Digite ou cole abaixo para ativar o Premium.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#737B87"), FontSize = 12
                },
                _activationKey,
                RowButton("Ativar minha chave", RedeemKey_Click, "#27865A"),
                new TextBlock
                {
                    Text = "Ainda não tem uma chave? Adquira com a Future Soluções.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#454B55"), FontSize = 13,
                    Margin = new Thickness(0, 5, 0, 0)
                },
                RowButton("Adquirir com a Future", OpenFuture_Click, "#343A46"),
                RowButton("Conhecer planos e contratar", OpenPlans_Click, "#6741B8")
            }
        }));

        root.Children.Add(_status);
        var close = new Button
        {
            Content = "Fechar", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 9), Background = Brush.Parse("#E2E5E9"),
            Foreground = Brush.Parse("#343A46"), BorderThickness = new Thickness(0)
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = root
        };
        SetRegisterMode(false);
        Opened += async (_, _) => await RefreshAccountLabelAsync();
    }

    private void SetRegisterMode(bool register)
    {
        _registerMode = register;
        _name.IsVisible = register && !_signedIn;
        _loginTab.IsVisible = !_signedIn;
        _registerTab.IsVisible = !_signedIn;
        _email.IsVisible = !_signedIn;
        _password.IsVisible = !_signedIn;
        _submitButton.IsVisible = !_signedIn;
        _signOutButton.IsVisible = _signedIn;
        _submitButton.Content = register ? "Criar minha conta" : "Entrar";
        _submitButton.Click -= SignIn_Click;
        _submitButton.Click -= CreateAccount_Click;
        _submitButton.Click += register ? CreateAccount_Click : SignIn_Click;
        _loginTab.Background = Brush.Parse(register ? "#F0EDF5" : "#6741B8");
        _loginTab.Foreground = register ? Brush.Parse("#514568") : Brushes.White;
        _registerTab.Background = Brush.Parse(register ? "#6741B8" : "#F0EDF5");
        _registerTab.Foreground = register ? Brushes.White : Brush.Parse("#514568");
    }

    private static Button TabButton(string label, bool selected) => new()
    {
        Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(12, 10), Background = Brush.Parse(selected ? "#6741B8" : "#F0EDF5"),
        Foreground = selected ? Brushes.White : Brush.Parse("#514568"),
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7)
    };

    private static Border Card(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        stack.Children.Add(content);
        return new Border
        {
            Background = Brushes.White, BorderBrush = Brush.Parse("#E0E3E8"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16), Child = stack
        };
    }

    private static Button RowButton(string label, EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler, string color)
    {
        var button = new Button
        {
            Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 9), Background = Brush.Parse(color),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        button.Click += handler;
        return button;
    }

    private async void CreateAccount_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _status.Text = "Informe seu nome completo para criar a conta.";
            return;
        }
        await RunAsync(async () => await _auth.SignUpAsync(_name.Text ?? "", _email.Text ?? "", _password.Text ?? ""));
        await RefreshAccountLabelAsync();
    }

    private async void SignIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunAsync(async () => await _auth.SignInAsync(_email.Text ?? "", _password.Text ?? ""));
        await RefreshAccountLabelAsync();
    }

    private async void RedeemKey_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (!_auth.IsSignedIn) throw new InvalidOperationException("Entre na sua conta antes de ativar a chave.");
            var message = await _auth.RedeemKeyAsync(_activationKey.Text ?? "");
            _activationKey.Text = "";
            return message;
        });
    }

    private async void SignOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _auth.SignOutAsync();
            return "Você saiu da sua conta.";
        });
        await RefreshAccountLabelAsync();
    }

    private void OpenFuture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenUrl("https://futuresolucoes.com.br/");

    private async void OpenPlans_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await new PlansWindow(_auth).ShowDialog(this);
        await RefreshAccountLabelAsync();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    private async Task RunAsync(Func<Task<string>> operation)
    {
        _status.Text = "Processando...";
        try { _status.Text = await operation(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task RefreshAccountLabelAsync()
    {
        await _auth.InitializeAsync();
        _signedIn = _auth.IsSignedIn;
        _loginTab.IsVisible = !_signedIn;
        _registerTab.IsVisible = !_signedIn;
        _name.IsVisible = !_signedIn && _registerMode;
        _email.IsVisible = !_signedIn;
        _password.IsVisible = !_signedIn;
        _submitButton.IsVisible = !_signedIn;
        _signOutButton.IsVisible = _signedIn;
        var displayName = string.IsNullOrWhiteSpace(_auth.FullName) ? _auth.Email : _auth.FullName;
        _account.Text = _signedIn ? $"Você está logado como {displayName}." : "Você ainda não entrou.";
    }

    private sealed class PlansWindow : Window
    {
        private readonly SupabaseAuthService _auth;
        private readonly TextBlock _premiumPrice = new()
        {
            Text = "Preço mensal ainda não cadastrado",
            FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#6741B8")
        };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5E6470") };

        public PlansWindow(SupabaseAuthService auth)
        {
            _auth = auth;
            Title = "Planos AssinaPDF";
            Width = 820;
            Height = 650;
            MinWidth = 680;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush.Parse("#F3F4F6");

            var free = PlanCard("FREE", "R$ 0,00", new[]
            {
                "Abrir e visualizar PDFs",
                "Acessar recursos gratuitos do aplicativo",
                "Assinatura e validação conforme disponibilidade do plano",
                "Sem licença Premium ativa"
            }, false);
            var premiumContent = new StackPanel { Spacing = 10 };
            premiumContent.Children.Add(new TextBlock
            {
                Text = "PREMIUM", FontSize = 16, FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#6741B8")
            });
            premiumContent.Children.Add(_premiumPrice);
            premiumContent.Children.Add(new TextBlock
            {
                Text = "Cobrança mensal recorrente. O valor é definido pela Future Soluções.",
                FontSize = 12, Foreground = Brush.Parse("#737B87"), TextWrapping = TextWrapping.Wrap
            });
            foreach (var feature in new[]
            {
                "Recursos Premium liberados com licença ativa",
                "Ativação por chave ou contratação online",
                "Licença vinculada à sua conta e dispositivo",
                "Suporte da Future Soluções"
            })
                premiumContent.Children.Add(Feature(feature));
            var buy = RowButton("Contratar Premium", Buy_Click, "#6741B8");
            premiumContent.Children.Add(buy);
            premiumContent.Children.Add(RowButton("Copiar informações do Premium", CopyPremium_Click, "#343A46"));
            var premium = new Border
            {
                Background = Brushes.White, BorderBrush = Brush.Parse("#D8CBEF"),
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20), Child = premiumContent
            };

            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
            columns.Children.Add(free);
            Grid.SetColumn(premium, 1);
            columns.Children.Add(premium);
            var root = new StackPanel { Spacing = 14, Margin = new Thickness(22) };
            root.Children.Add(new TextBlock
            {
                Text = "Escolha seu plano", FontSize = 25, FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#292D36")
            });
            root.Children.Add(new TextBlock
            {
                Text = "Compare os recursos e escolha a opção ideal para usar o AssinaPDF.",
                FontSize = 13, Foreground = Brush.Parse("#737B87"), TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(columns);
            root.Children.Add(_status);
            var close = new Button { Content = "Fechar", HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => Close();
            root.Children.Add(close);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        }

        private static Border PlanCard(string title, string price, IEnumerable<string> features, bool premium)
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 16, FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(premium ? "#6741B8" : "#454B55")
            });
            stack.Children.Add(new TextBlock
            {
                Text = price, FontSize = 20, FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#292D36")
            });
            foreach (var feature in features) stack.Children.Add(Feature(feature));
            return new Border
            {
                Background = Brushes.White, BorderBrush = Brush.Parse("#E0E3E8"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20), Child = stack
            };
        }

        private static TextBlock Feature(string text) => new()
        {
            Text = "✓  " + text, FontSize = 12, Foreground = Brush.Parse("#5E6470"),
            TextWrapping = TextWrapping.Wrap
        };

        private async void Buy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                if (!_auth.IsSignedIn)
                    throw new InvalidOperationException("Entre ou cadastre sua conta antes de contratar.");
                var url = await _auth.CreateMonthlyCheckoutAsync();
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                _status.Text = "A página segura de contratação foi aberta no navegador.";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        }

        private async void CopyPremium_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            const string details = "AssinaPDF Premium — plano mensal recorrente. Recursos Premium mediante licença ativa. Ativação por chave ou contratação online. Licença vinculada à conta e ao dispositivo. Valor mensal: consulte a Future Soluções.";
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) throw new InvalidOperationException("Área de transferência indisponível.");
                await clipboard.SetTextAsync(details);
                _status.Text = "Informações do Premium copiadas.";
            }
            catch { _status.Text = "Não foi possível copiar as informações neste dispositivo."; }
        }
    }
}
