using PdfSharp.Drawing;

namespace Danfe.NFe.Core.Graphics
{
    /// <summary>
    /// Contexto compartilhado de medição de fontes.
    /// XGraphics.CreateMeasureContext cria um XGraphics sem página associada,
    /// usado apenas para medir strings (MeasureString). Reutilizar uma instância
    /// evita criação repetitiva durante o layout do DANFE.
    /// </summary>
    internal static class FontMetrics
    {
        private static readonly object _Lock = new object();
        private static XGraphics _MeasureContext;

        private static XGraphics MeasureContext
        {
            get
            {
                if (_MeasureContext == null)
                {
                    lock (_Lock)
                    {
                        if (_MeasureContext == null)
                        {
                            _MeasureContext = XGraphics.CreateMeasureContext(
                                new XSize(1000, 1000),
                                XGraphicsUnit.Point,
                                XPageDirection.Downwards);
                        }
                    }
                }
                return _MeasureContext;
            }
        }

        /// <summary>
        /// Mede a largura de uma string em pontos.
        /// </summary>
        public static double MeasureWidthPt(XFont font, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            lock (_Lock)
            {
                return MeasureContext.MeasureString(text, font).Width;
            }
        }
    }
}
