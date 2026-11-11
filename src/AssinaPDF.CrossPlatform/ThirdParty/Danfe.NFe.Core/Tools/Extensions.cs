using System.Drawing;
using System.Text;

namespace Danfe.NFe.Core.Tools.Extensions
{
    internal static class Extentions
    {
        private const float PointFactor = 72F / 25.4F;

        /// <summary>
        /// Converte milímetros para pontos PDF.
        /// </summary>
        public static float ToPoint(this float mm) => PointFactor * mm;

        /// <summary>
        /// Converte pontos PDF para milímetros.
        /// </summary>
        public static float ToMm(this float point) => point / PointFactor;

        /// <summary>
        /// Converte milímetros para pontos PDF (double).
        /// </summary>
        public static double ToPoint(this double mm) => PointFactor * mm;

        /// <summary>
        /// Converte pontos PDF para milímetros (double).
        /// </summary>
        public static double ToMm(this double point) => point / PointFactor;

        public static SizeF ToMm(this SizeF s) => new SizeF(s.Width.ToMm(), s.Height.ToMm());

        public static SizeF ToPointMeasure(this SizeF s) => new SizeF(s.Width.ToPoint(), s.Height.ToPoint());

        public static RectangleF InflatedRetangle(this RectangleF rect, float top, float button, float horizontal)
        {
            return new RectangleF(rect.X + horizontal, rect.Y + top, rect.Width - 2 * horizontal, rect.Height - top - button);
        }

        public static RectangleF InflatedRetangle(this RectangleF rect, float value) => rect.InflatedRetangle(value, value, value);

        public static RectangleF ToPointMeasure(this RectangleF r) => new RectangleF(r.X.ToPoint(), r.Y.ToPoint(), r.Width.ToPoint(), r.Height.ToPoint());

        public static RectangleF CutTop(this RectangleF r, float height) => new RectangleF(r.X, r.Y + height, r.Width, r.Height - height);

        public static RectangleF CutBottom(this RectangleF r, float height) => new RectangleF(r.X, r.Y, r.Width, r.Height - height);

        public static RectangleF CutLeft(this RectangleF r, float width) => new RectangleF(r.X + width, r.Y, r.Width - width, r.Height);

        public static PointF ToPointMeasure(this PointF r) => new PointF(r.X.ToPoint(), r.Y.ToPoint());

        public static StringBuilder AppendChaveValor(this StringBuilder sb, string chave, string valor)
        {
            if (sb.Length > 0) sb.Append(' ');
            return sb.Append(chave).Append(": ").Append(valor);
        }
    }
}
