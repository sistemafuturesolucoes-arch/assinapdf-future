using System;
using PdfSharp.Fonts;

namespace Danfe.NFe.Core.Graphics
{
    /// <summary>
    /// Registra o <see cref="DanfeFontResolver"/> como <see cref="GlobalFontSettings.FallbackFontResolver"/>
    /// do PDFsharp uma única vez. Como fallback, ele só é consultado quando o
    /// <see cref="GlobalFontSettings.FontResolver"/> do consumidor (se houver) não resolve a fonte.
    /// </summary>
    internal static class DanfeFontResolverSetup
    {
        private static readonly object _Lock = new object();
        private static volatile bool _Initialized;

        /// <summary>
        /// Garante que o FontResolver global está configurado. Idempotente e thread-safe.
        /// Pode ser chamado múltiplas vezes; só age na primeira invocação.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_Initialized) return;
            lock (_Lock)
            {
                if (_Initialized) return;

                // Se o consumidor já definiu um FallbackFontResolver, respeitar.
                if (GlobalFontSettings.FallbackFontResolver == null)
                {
                    try
                    {
                        GlobalFontSettings.FallbackFontResolver = new DanfeFontResolver();
                    }
                    catch (InvalidOperationException)
                    {
                        // O PDFsharp não aceita trocar resolver depois que alguma fonte já foi usada.
                    }
                }

                _Initialized = true;
            }
        }
    }
}
