using Avalonia;
using AssinaPDF.MacPrototype.Services;
using System;

namespace AssinaPDF.MacPrototype;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // PDFsharp allows its global font resolver to be configured only before
        // the first font is created. Do this before Avalonia or any PDF feature starts.
        NfcePdfFontResolverBootstrap.EnsureConfigured();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
