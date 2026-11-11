using PdfSharp.Drawing;
using Danfe.NFe.Core.Graphics;

namespace Danfe.NFe.Core.Elementos
{
    /// <summary>
    /// Coleção de fontes e medidas a serem compartilhadas entre os elementos básicos.
    /// </summary>
    internal class Estilo
    {
        public float PaddingSuperior { get; set; }
        public float PaddingInferior { get; set; }
        public float PaddingHorizontal { get; set; }
        public float FonteTamanhoMinimo { get; set; }

        /// <summary>
        /// Família de fonte (ex.: "Times New Roman").
        /// </summary>
        public string FamiliaFonte { get; set; }

        public Fonte FonteCampoCabecalho { get; private set; }
        public Fonte FonteCampoConteudo { get; private set; }
        public Fonte FonteCampoConteudoNegrito { get; private set; }
        public Fonte FonteBlocoCabecalho { get; private set; }
        public Fonte FonteNumeroFolhas { get; private set; }

        public Estilo(string familiaFonte, float tamanhoFonteCampoCabecalho = 6, float tamanhoFonteConteudo = 10)
        {
            PaddingHorizontal = 0.75F;
            PaddingSuperior = 0.65F;
            PaddingInferior = 0.3F;

            FamiliaFonte = familiaFonte;

            FonteCampoCabecalho = CriarFonteRegular(tamanhoFonteCampoCabecalho);
            FonteCampoConteudo = CriarFonteRegular(tamanhoFonteConteudo);
            FonteCampoConteudoNegrito = CriarFonteNegrito(tamanhoFonteConteudo);
            FonteBlocoCabecalho = CriarFonteRegular(7);
            FonteNumeroFolhas = CriarFonteNegrito(10F);
            FonteTamanhoMinimo = 5.75F;
        }

        public Fonte CriarFonteRegular(float emSize) => new Fonte(FamiliaFonte, XFontStyleEx.Regular, emSize);
        public Fonte CriarFonteNegrito(float emSize) => new Fonte(FamiliaFonte, XFontStyleEx.Bold, emSize);
        public Fonte CriarFonteItalico(float emSize) => new Fonte(FamiliaFonte, XFontStyleEx.Italic, emSize);
    }
}
