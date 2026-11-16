using System.Reflection;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Inicializa o resolvedor de fontes do DanfeSharp antes de qualquer outro
/// componente usar PDFsharp. O PDFsharp mantém o resolvedor como estado global
/// e não permite instalá-lo depois que a primeira fonte já foi criada.
/// </summary>
public static class NfcePdfFontResolverBootstrap
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void EnsureConfigured()
    {
        lock (Sync)
        {
            if (_initialized)
                return;

            var assembly = typeof(DanfeSharp.NFCe.Danfce).Assembly;
            var resolverType = assembly.GetType(
                "DanfeSharp.Graphics.ResolvedorFontes",
                throwOnError: true,
                ignoreCase: false)!;

            var configure = resolverType.GetMethod(
                "Configurar",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (configure is null)
                throw new InvalidOperationException(
                    "A versão instalada do DanfeSharp.Next não expõe o inicializador de fontes esperado. " +
                    "Mantenha a versão 3.4.0 ou atualize este bootstrap junto com a biblioteca.");

            try
            {
                configure.Invoke(null, null);

                // DanfeSharp.Next installs the primary resolver. Register the embedded
                // Liberation Serif resolver as PDFsharp fallback so MDF-e and other
                // PDFsharp consumers can resolve portable fonts on Windows and macOS.
                Danfe.NFe.Core.Graphics.DanfeFontResolverSetup.EnsureInitialized();
                _initialized = true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException(
                    "Não foi possível inicializar o resolvedor de fontes do DANFCE antes do PDFsharp.",
                    ex.InnerException);
            }
        }
    }
}
