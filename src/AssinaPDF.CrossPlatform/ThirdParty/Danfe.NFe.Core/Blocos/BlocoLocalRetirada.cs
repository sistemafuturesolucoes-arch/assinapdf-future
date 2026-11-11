using System;
using System.Collections.Generic;
using System.Text;
using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Modelo;

namespace Danfe.NFe.Core.Blocos
{
    class BlocoLocalRetirada : BlocoLocalEntregaRetirada
    {
        public BlocoLocalRetirada(DanfeViewModel viewModel, Estilo estilo)
            : base(viewModel, estilo, viewModel.LocalRetirada)
        {
        }

        public override string Cabecalho => "Informações do local de retirada";
    }
}
