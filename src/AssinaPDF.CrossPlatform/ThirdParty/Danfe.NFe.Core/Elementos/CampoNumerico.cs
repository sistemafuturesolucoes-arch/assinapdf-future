using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Graphics;
using Danfe.NFe.Core.Tools;

namespace Danfe.NFe.Core.Elementos
{
    /// <summary>
    /// Campo para valores numéricos.
    /// </summary>
    internal class CampoNumerico : Campo
    {
        private double? ConteudoNumerico { get; set; }
        public int CasasDecimais { get; set; }

        public CampoNumerico(string cabecalho, double? conteudoNumerico, Estilo estilo, int casasDecimais = 2) : base(cabecalho, null, estilo, AlinhamentoHorizontal.Direita)
        {
            CasasDecimais = casasDecimais;
            ConteudoNumerico = conteudoNumerico;
        }

        protected override void DesenharConteudo(Gfx gfx)
        {
            base.Conteudo = ConteudoNumerico.HasValue ? ConteudoNumerico.Value.ToString($"N{CasasDecimais}", Formatador.Cultura) : null;
            base.DesenharConteudo(gfx);
        }
    }
}
