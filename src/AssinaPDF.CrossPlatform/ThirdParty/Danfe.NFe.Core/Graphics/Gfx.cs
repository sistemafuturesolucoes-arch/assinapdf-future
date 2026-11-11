using System;
using System.Drawing;
using PdfSharp.Drawing;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Tools.Extensions;

namespace Danfe.NFe.Core.Graphics
{
    /// <summary>
    /// Fachada de desenho sobre XGraphics (PDFsharp).
    /// Todas as coordenadas recebidas pelo Gfx são em milímetros;
    /// internamente convertemos para pontos antes de delegar ao XGraphics.
    /// </summary>
    internal class Gfx
    {
        /// <summary>
        /// XGraphics do PDFsharp associado à página.
        /// </summary>
        public XGraphics XGraphics { get; private set; }

        /// <summary>
        /// Cor usada para preencher o texto. Padrão = preto.
        /// </summary>
        public XBrush TextBrush { get; set; } = XBrushes.Black;

        /// <summary>
        /// Cor usada para preencher formas (retângulos). Padrão = preto.
        /// </summary>
        public XBrush FillBrush { get; set; } = XBrushes.Black;

        /// <summary>
        /// Caneta usada para traçar. Padrão = preto com 0.25f.
        /// </summary>
        public XPen StrokePen { get; set; }

        public Gfx(XGraphics xGraphics)
        {
            XGraphics = xGraphics ?? throw new ArgumentNullException(nameof(xGraphics));
            StrokePen = new XPen(XColors.Black, 0.25f.ToPoint());
        }

        /// <summary>
        /// Ascent ratio da Times Type 1 usada pelo PDFClown (AFM ascent=683 / 1000em).
        /// Ao posicionar a baseline em <c>y_top + Tamanho * 0.683</c> conseguimos que
        /// o topo visual do caractere fique em <c>y_top</c>, idêntico ao comportamento
        /// do PDFClown com <c>ShowText(y, YAlignment.Top)</c>.
        /// </summary>
        private const double PdfClownTimesAscentRatio = 0.683;

        /// <summary>
        /// Desenha uma string dentro de um retângulo (em mm), aplicando alinhamento
        /// horizontal e vertical. O alinhamento horizontal é delegado ao PDFsharp
        /// via <see cref="XStringFormat"/> — isso evita erros de centralização que
        /// surgiriam ao medir a largura em um contexto separado (CreateMeasureContext)
        /// e passá-la à <c>DrawString</c>, pois a fonte usada na medição pode
        /// divergir sutilmente da fonte resolvida na página real.
        /// </summary>
        internal void DrawString(string str, RectangleF rect, Fonte fonte,
            AlinhamentoHorizontal ah = AlinhamentoHorizontal.Esquerda,
            AlinhamentoVertical av = AlinhamentoVertical.Topo)
        {
            if (string.IsNullOrEmpty(str)) return;
            if (fonte == null) throw new ArgumentNullException(nameof(fonte));
            if (fonte.Tamanho <= 0) throw new ArgumentOutOfRangeException(nameof(fonte));
            CheckRectangle(rect);

            // Y: calculamos o "top" visual do texto baseado no alinhamento vertical.
            float yTopMm;
            if (av == AlinhamentoVertical.Base)
                yTopMm = rect.Bottom - fonte.AlturaLinha;
            else if (av == AlinhamentoVertical.Centro)
                yTopMm = rect.Y + (rect.Height - fonte.AlturaLinha) / 2F;
            else // Topo
                yTopMm = rect.Y;

            // X: âncora horizontal para o alinhamento.
            //  - Esquerda: âncora = X esquerdo do rect; Alignment.Near
            //  - Centro:   âncora = X central do rect;  Alignment.Center
            //  - Direita:  âncora = X direito do rect;  Alignment.Far
            double xAnchorPt;
            XStringAlignment xAlign;
            switch (ah)
            {
                case AlinhamentoHorizontal.Centro:
                    xAnchorPt = (rect.X + rect.Width / 2F).ToPoint();
                    xAlign = XStringAlignment.Center;
                    break;
                case AlinhamentoHorizontal.Direita:
                    xAnchorPt = rect.Right.ToPoint();
                    xAlign = XStringAlignment.Far;
                    break;
                default:
                    xAnchorPt = rect.X.ToPoint();
                    xAlign = XStringAlignment.Near;
                    break;
            }

            // Baseline = topMm (em pt) + ascent em pontos (PDFClown-Times compat).
            double baselinePt = yTopMm.ToPoint() + fonte.Tamanho * PdfClownTimesAscentRatio;

            var fmt = new XStringFormat
            {
                Alignment = xAlign,
                LineAlignment = XLineAlignment.BaseLine
            };

            XGraphics.DrawString(str, fonte.FonteInterna, TextBrush,
                new XPoint(xAnchorPt, baselinePt),
                fmt);
        }

        /// <summary>
        /// Desenha a string tratando <paramref name="point"/> (mm) como top-left visual
        /// do texto — compatível com o layout original calibrado para PDFClown.
        /// Uso interno (ex.: TextBlock que já calculou posições linha-a-linha).
        /// </summary>
        public void ShowText(string text, PointF point, Fonte fonte)
        {
            CheckPoint(point);

            double baselinePt = point.Y.ToPoint() + fonte.Tamanho * PdfClownTimesAscentRatio;

            var fmt = new XStringFormat
            {
                Alignment = XStringAlignment.Near,
                LineAlignment = XLineAlignment.BaseLine
            };

            XGraphics.DrawString(text, fonte.FonteInterna, TextBrush,
                new XPoint(point.X.ToPoint(), baselinePt),
                fmt);
        }

        /// <summary>
        /// Desenha uma imagem (logo raster/PDF) centralizada e proporcional dentro de um retângulo.
        /// </summary>
        public void ShowXObject(XImage xobj, RectangleF r)
        {
            if (xobj == null) throw new ArgumentNullException(nameof(xobj));
            CheckRectangle(r);

            // Tamanho do XImage em pontos (convertemos para mm).
            SizeF xs = new SizeF((float)xobj.PointWidth.ToMm(), (float)xobj.PointHeight.ToMm());
            PointF p = new PointF();
            SizeF s = new SizeF();

            if (r.Height >= r.Width)
            {
                if (xs.Height >= xs.Width)
                {
                    s.Height = r.Height;
                    s.Width = (s.Height * xs.Width) / xs.Height;
                }
                else
                {
                    s.Width = r.Width;
                    s.Height = (s.Width * xs.Height) / xs.Width;
                }
            }
            else
            {
                if (xs.Height >= xs.Width)
                {
                    s.Width = r.Width;
                    s.Height = (s.Width * xs.Height) / xs.Width;
                }
                else
                {
                    s.Height = r.Height;
                    s.Width = (s.Height * xs.Width) / xs.Height;
                }
            }

            p.X = r.X + Math.Abs(r.Width - s.Width) / 2F;
            p.Y = r.Y + Math.Abs(r.Height - s.Height) / 2F;

            XGraphics.DrawImage(xobj,
                new XRect(p.X.ToPoint(), p.Y.ToPoint(), s.Width.ToPoint(), s.Height.ToPoint()));
        }

        /// <summary>
        /// Traça o retângulo (em mm) com a <see cref="StrokePen"/>, ajustando a largura da linha.
        /// </summary>
        public void StrokeRectangle(RectangleF rect, float width)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            CheckRectangle(rect);
            var pen = new XPen(StrokePen.Color, width.ToPoint());
            XGraphics.DrawRectangle(pen,
                rect.X.ToPoint(), rect.Y.ToPoint(), rect.Width.ToPoint(), rect.Height.ToPoint());
        }

        /// <summary>
        /// Define a largura da caneta padrão.
        /// </summary>
        public void SetLineWidth(float w)
        {
            if (w < 0) throw new ArgumentOutOfRangeException(nameof(w));
            StrokePen = new XPen(StrokePen.Color, w.ToPoint());
        }

        /// <summary>
        /// Traça um retângulo usando a <see cref="StrokePen"/> padrão.
        /// </summary>
        public void DrawRectangle(RectangleF rect)
        {
            CheckRectangle(rect);
            XGraphics.DrawRectangle(StrokePen,
                rect.X.ToPoint(), rect.Y.ToPoint(), rect.Width.ToPoint(), rect.Height.ToPoint());
        }

        public void DrawRectangle(float x, float y, float w, float h) => DrawRectangle(new RectangleF(x, y, w, h));

        /// <summary>
        /// Preenche o retângulo usando <see cref="FillBrush"/> (ex: módulos do código de barras).
        /// </summary>
        public void FillRectangle(RectangleF rect)
        {
            CheckRectangle(rect);
            XGraphics.DrawRectangle(FillBrush,
                rect.X.ToPoint(), rect.Y.ToPoint(), rect.Width.ToPoint(), rect.Height.ToPoint());
        }

        public void FillRectangle(float x, float y, float w, float h) => FillRectangle(new RectangleF(x, y, w, h));

        /// <summary>
        /// Desenha uma linha entre dois pontos em mm.
        /// </summary>
        public void DrawLine(PointF p1, PointF p2, XPen pen = null)
        {
            var usedPen = pen ?? StrokePen;
            XGraphics.DrawLine(usedPen,
                p1.X.ToPoint(), p1.Y.ToPoint(),
                p2.X.ToPoint(), p2.Y.ToPoint());
        }

        /// <summary>
        /// Desenha uma linha tracejada entre dois pontos em mm.
        /// </summary>
        public void DrawDashedLine(PointF p1, PointF p2, XColor color, float lineWidth, double[] dashPattern)
        {
            var pen = new XPen(color, lineWidth.ToPoint())
            {
                DashStyle = XDashStyle.Custom,
                DashPattern = dashPattern
            };
            XGraphics.DrawLine(pen,
                p1.X.ToPoint(), p1.Y.ToPoint(),
                p2.X.ToPoint(), p2.Y.ToPoint());
        }

        /// <summary>
        /// Salva o estado gráfico (transformações, clipping). Use em <c>using</c>.
        /// </summary>
        public IDisposable SaveState() => new GfxState(XGraphics);

        /// <summary>
        /// Rotaciona o sistema de coordenadas em graus em torno de um ponto (em mm).
        /// Delega para <see cref="XGraphics.RotateAtTransform(double, XPoint)"/> que aplica a sequência
        /// correta de transformações (T · R · T⁻¹) respeitando a ordem Prepend interna
        /// do PDFsharp. Compor Translate/Rotate/Translate manualmente não funciona
        /// porque cada chamada é prepended, invertendo a ordem efetiva na CTM.
        /// </summary>
        public void RotateTransform(double angleDegrees, PointF centerMm)
        {
            XGraphics.RotateAtTransform(angleDegrees,
                new XPoint(centerMm.X.ToPoint(), centerMm.Y.ToPoint()));
        }

        private void CheckRectangle(RectangleF r)
        {
            if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0) throw new ArgumentException(nameof(r));
        }

        private void CheckPoint(PointF p)
        {
            if (p.X < 0 || p.Y < 0) throw new ArgumentException(nameof(p));
        }

        // Mantidos como no-op para manter a assinatura usada pelo código legado.
        // No PDFsharp o desenho é imediato — não há buffer explícito.
        public void Stroke() { /* no-op: desenho é imediato no XGraphics */ }
        public void Flush() { /* no-op: desenho é imediato no XGraphics */ }
        public void Fill() { /* no-op: uso DrawRectangle(brush, ...) via FillRectangle */ }

        /// <summary>
        /// Token Disposable para Save/Restore do XGraphics.
        /// </summary>
        private sealed class GfxState : IDisposable
        {
            private readonly XGraphics _g;
            private readonly XGraphicsState _state;

            public GfxState(XGraphics g)
            {
                _g = g;
                _state = g.Save();
            }

            public void Dispose() => _g.Restore(_state);
        }
    }
}
