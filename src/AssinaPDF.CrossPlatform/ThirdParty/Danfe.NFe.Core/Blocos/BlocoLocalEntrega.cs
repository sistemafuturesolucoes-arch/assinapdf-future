using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Modelo;

namespace Danfe.NFe.Core.Blocos
{
    class BlocoLocalEntrega : BlocoLocalEntregaRetirada
    {
        public BlocoLocalEntrega(DanfeViewModel viewModel, Estilo estilo)
            : base(viewModel, estilo, viewModel.LocalEntrega)
        {
        }

        public override string Cabecalho => "Informações do local de entrega";
    }
}
