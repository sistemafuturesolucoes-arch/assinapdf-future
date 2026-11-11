using System;
using System.Drawing;
using System.Linq;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Danfe.NFe.Core.Blocos;
using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Graphics;
using Danfe.NFe.Core.Tools.Extensions;

namespace Danfe.NFe.Core
{
    internal class DanfePagina : IDisposable
    {
        public DanfeDoc Danfe { get; private set; }
        public PdfPage PdfPage { get; private set; }
        public XGraphics XGraphics { get; private set; }
        public Gfx Gfx { get; private set; }
        public RectangleF RetanguloNumeroFolhas { get; set; }
        public RectangleF RetanguloCorpo { get; private set; }
        public RectangleF RetanguloDesenhavel { get; private set; }
        public RectangleF RetanguloCreditos { get; private set; }
        public RectangleF Retangulo { get; private set; }

        private bool _Disposed;

        public DanfePagina(DanfeDoc danfe)
        {
            Danfe = danfe ?? throw new ArgumentNullException(nameof(danfe));
            PdfPage = Danfe.PdfDocument.AddPage();

            if (Danfe.ViewModel.Orientacao == Orientacao.Retrato)
            {
                Retangulo = new RectangleF(0, 0, Constantes.A4Largura, Constantes.A4Altura);
                PdfPage.Size = PageSize.A4;
                PdfPage.Orientation = PageOrientation.Portrait;
            }
            else
            {
                Retangulo = new RectangleF(0, 0, Constantes.A4Altura, Constantes.A4Largura);
                PdfPage.Size = PageSize.A4;
                PdfPage.Orientation = PageOrientation.Landscape;
            }

            // PDFsharp cria o XGraphics ancorado à página; trabalhamos em pontos.
            XGraphics = XGraphics.FromPdfPage(PdfPage);
            Gfx = new Gfx(XGraphics);

            RetanguloDesenhavel = Retangulo.InflatedRetangle(Danfe.ViewModel.Margem);
            RetanguloCreditos = new RectangleF(
                RetanguloDesenhavel.X,
                RetanguloDesenhavel.Bottom + Danfe.EstiloPadrao.PaddingSuperior,
                RetanguloDesenhavel.Width,
                Retangulo.Height - RetanguloDesenhavel.Height - Danfe.EstiloPadrao.PaddingSuperior);
        }

        public void DesenharCreditos()
        {
            Gfx.DrawString(Strings.TextoCreditos, RetanguloCreditos,
                Danfe.EstiloPadrao.CriarFonteItalico(6), AlinhamentoHorizontal.Direita);
        }

        private void DesenharCanhoto()
        {
            if (Danfe.ViewModel.QuantidadeCanhotos == 0) return;

            var canhoto = Danfe.Canhoto;
            canhoto.SetPosition(RetanguloDesenhavel.Location);

            if (Danfe.ViewModel.Orientacao == Orientacao.Retrato)
            {
                canhoto.Width = RetanguloDesenhavel.Width;

                for (int i = 0; i < Danfe.ViewModel.QuantidadeCanhotos; i++)
                {
                    canhoto.Draw(Gfx);
                    canhoto.Y += canhoto.Height;
                }

                RetanguloDesenhavel = RetanguloDesenhavel.CutTop(canhoto.Height * Danfe.ViewModel.QuantidadeCanhotos);
            }
            else
            {
                canhoto.Width = RetanguloDesenhavel.Height;

                // Em paisagem, rotacionamos o canhoto 90° para ficar na lateral esquerda.
                //
                // A rotação leva o retângulo horizontal (X, Y, W, H) para um vertical
                // (X, Y, H, W) no mesmo canto superior-esquerdo, com texto lendo de
                // BAIXO para CIMA (quando a folha está em paisagem). Para isso, o
                // pivot é (X + W/2, Y + W/2) e o ângulo é -90° no sistema Y-down do
                // PDFsharp (que corresponde a uma rotação visual anti-horária).
                //
                // A fórmula antiga (pivot (0, W+X+Y) com +90) funcionava no PDFClown
                // porque o sistema de coordenadas + composição de CTM eram diferentes.
                var pivotX = canhoto.X + canhoto.Width / 2F;
                var pivotY = canhoto.Y + canhoto.Width / 2F;
                var pivot = new PointF(pivotX, pivotY);

                using (Gfx.SaveState())
                {
                    Gfx.RotateTransform(-90, pivot);

                    for (int i = 0; i < Danfe.ViewModel.QuantidadeCanhotos; i++)
                    {
                        canhoto.Draw(Gfx);
                        canhoto.Y += canhoto.Height;
                    }
                }

                RetanguloDesenhavel = RetanguloDesenhavel.CutLeft(canhoto.Height * Danfe.ViewModel.QuantidadeCanhotos);
            }
        }

        public void DesenhaNumeroPaginas(int n, int total)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
            if (total <= 0) throw new ArgumentOutOfRangeException(nameof(n));
            if (n > total) throw new ArgumentOutOfRangeException("O número da página atual deve ser menor que o total.");

            // Se a página já foi finalizada (XGraphics disposed), reabrimos
            // em modo "Append" para gravar o número de folhas no rodapé.
            bool recriado = false;
            if (_Disposed)
            {
                XGraphics = XGraphics.FromPdfPage(PdfPage, XGraphicsPdfPageOptions.Append);
                Gfx = new Gfx(XGraphics);
                _Disposed = false;
                recriado = true;
            }

            Gfx.DrawString($"Folha {n}/{total}", RetanguloNumeroFolhas,
                Danfe.EstiloPadrao.FonteNumeroFolhas, AlinhamentoHorizontal.Centro);

            if (recriado) Dispose();
        }

        public void DesenharAvisoHomologacao()
        {
            var ts = new TextStack(RetanguloCorpo) { AlinhamentoVertical = AlinhamentoVertical.Centro, AlinhamentoHorizontal = AlinhamentoHorizontal.Centro, LineHeightScale = 0.9F }
                        .AddLine("SEM VALOR FISCAL", Danfe.EstiloPadrao.CriarFonteRegular(48))
                        .AddLine("AMBIENTE DE HOMOLOGAÇÃO", Danfe.EstiloPadrao.CriarFonteRegular(30));

            using (Gfx.SaveState())
            {
                // Cinza médio (0.35, 0.35, 0.35) para o aviso.
                var prevBrush = Gfx.TextBrush;
                Gfx.TextBrush = new XSolidBrush(XColor.FromArgb(89, 89, 89));
                ts.Draw(Gfx);
                Gfx.TextBrush = prevBrush;
            }
        }

        public void DesenharBlocos(bool isPrimeirapagina = false)
        {
            if (isPrimeirapagina && Danfe.ViewModel.QuantidadeCanhotos > 0) DesenharCanhoto();

            var blocos = isPrimeirapagina ? Danfe._Blocos : Danfe._Blocos.Where(x => x.VisivelSomentePrimeiraPagina == false);

            foreach (var bloco in blocos)
            {
                bloco.Width = RetanguloDesenhavel.Width;

                if (bloco.Posicao == PosicaoBloco.Topo)
                {
                    bloco.SetPosition(RetanguloDesenhavel.Location);
                    RetanguloDesenhavel = RetanguloDesenhavel.CutTop(bloco.Height);
                }
                else
                {
                    bloco.SetPosition(RetanguloDesenhavel.X, RetanguloDesenhavel.Bottom - bloco.Height);
                    RetanguloDesenhavel = RetanguloDesenhavel.CutBottom(bloco.Height);
                }

                bloco.Draw(Gfx);

                if (bloco is BlocoIdentificacaoEmitente blocoId)
                {
                    RetanguloNumeroFolhas = blocoId.RetanguloNumeroFolhas;
                }
            }

            RetanguloCorpo = RetanguloDesenhavel;
        }

        public void Dispose()
        {
            if (_Disposed) return;
            XGraphics?.Dispose();
            XGraphics = null;
            Gfx = null;
            _Disposed = true;
        }
    }
}
