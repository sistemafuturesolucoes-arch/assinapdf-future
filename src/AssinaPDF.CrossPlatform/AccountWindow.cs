using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AssinaPDF.MacPrototype.Services;

namespace AssinaPDF.MacPrototype;

public sealed class AccountWindow : Window
{
    private readonly SupabaseAuthService _auth;
    private readonly TextBox _name = new() { Watermark = "Seu nome completo" };
    private readonly TextBox _email = new() { Watermark = "E-mail" };
    private readonly PasswordBox _password = new() { Watermark = "Senha (mínimo 6 caracteres)" };
    private readonly TextBox _activationKey = new() { Watermark = "FUT-XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5E6470") };
    private readonly TextBlock _account = new() { Foreground = Brush.Parse("#454B55"), FontWeight = FontWeight.SemiBold };

    public AccountWindow(SupabaseAuthService auth)
    {
        _auth = auth;
        Title = "Minha conta | AssinaPDF";
        Width = 540;
        Height = 690;
        MinWidth = 480;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#F3F4F6");
        CanResize = true;

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(28) };
        root.Children.Add(new TextBlock
        {
            Text = "Minha conta AssinaPDF", FontSize = 25, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#292D36")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Entre ou crie sua conta para ativar o Premium. Sua senha e seus certificados permanecem no seu computador.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#737B87"), FontSize = 13
        });
        root.Children.Add(Card("Conta", new StackPanel
        {
            Spacing = 9,
            Children =
            {
                _name,
                _email,
                _password,
                RowButton("Criar conta", CreateAccount_Click, "#6741B8"),
                RowButton("Entrar", SignIn_Click, "#343A46"),
                _account,
                RowButton("Sair da conta", SignOut_Click, "#737B87")
            }
        }));
        root.Children.Add(Card("Ativar plano Premium", new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock
                {
                    Text = "Cole a chave enviada pela Future Soluções após a confirmação do pagamento.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#737B87"), FontSize = 12
                },
                _activationKey,
                RowButton("Ativar minha chave", RedeemKey_Click, "#27865A")
            }
        }));
        root.Children.Add(_status);
        root.Children.Add(new Button
        {
            Content = "Fechar", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 9), Background = Brush.Parse("#E2E5E9"),
            Foreground = Brush.Parse("#343A46"), BorderThickness = new Thickness(0),
            Command = null
        }.Also(button => button.Click += (_, _) => Close()));

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = root
        };
        Opened += async (_, _) => await RefreshAccountLabelAsync();
    }

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
        await RunAsync(async () => await _auth.SignUpAsync(_name.Text ?? "", _email.Text ?? "", _password.Password ?? ""));
        await RefreshAccountLabelAsync();
    }

    private async void SignIn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RunAsync(async () => await _auth.SignInAsync(_email.Text ?? "", _password.Password ?? ""));
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

    private async Task RunAsync(Func<Task<string>> operation)
    {
        _status.Text = "Processando...";
        try { _status.Text = await operation(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task RefreshAccountLabelAsync()
    {
        await _auth.InitializeAsync();
        _account.Text = _auth.IsSignedIn ? $"Conectado: {_auth.Email}" : "Você ainda não entrou.";
    }
}

internal static class AccountWindowExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}