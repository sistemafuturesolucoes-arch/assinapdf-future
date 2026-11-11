using System;
using Danfe.NFe.Core.Elementos;
using Danfe.NFe.Core.Enumeracoes;
using Danfe.NFe.Core.Modelo;
using Danfe.NFe.Core.Tools;

namespace Danfe.NFe.Core.Blocos
{
    abstract class BlocoLocalEntregaRetirada : BlocoBase
    {
        public LocalEntregaRetiradaViewModel Model { get; private set; }

        public BlocoLocalEntregaRetirada(DanfeViewModel viewModel, Estilo estilo, LocalEntregaRetiradaViewModel localModel) : base(viewModel, estilo)
        {
            Model = localModel ?? throw new ArgumentNullException(nameof(localModel));

            AdicionarLinhaCampos()
            .ComCampo(Strings.NomeRazaoSocial, Model.NomeRazaoSocial)
            .ComCampo(Strings.CnpjCpf, Formatador.FormatarCpfCnpj(Model.CnpjCpf))
            .ComCampo(Strings.InscricaoEstadual, Model.InscricaoEstadual)
            .ComLarguras(0, 45F * Proporcao, 30F * Proporcao);

            AdicionarLinhaCampos()
            .ComCampo(Strings.Endereco, Model.Endereco)
            .ComCampo(Strings.BairroDistrito, Model.Bairro)
            .ComCampo(Strings.Cep, Formatador.FormatarCEP(Model.Cep))
            .ComLarguras(0, 45F * Proporcao, 30F * Proporcao);

            AdicionarLinhaCampos()
            .ComCampo(Strings.Municipio, Model.Municipio)
            .ComCampo(Strings.UF, Model.Uf)
            .ComCampo(Strings.FoneFax, Formatador.FormatarTelefone(Model.Telefone))
            .ComLarguras(0, 7F * Proporcao, 30F * Proporcao);
        }

        public override PosicaoBloco Posicao => PosicaoBloco.Topo;

    }
}

