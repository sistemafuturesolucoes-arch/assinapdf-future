# Central de XMLs NF-e — AssinaPDF Future

## Escopo desta implementação

A central é exclusiva do projeto `src/AssinaPDF.CrossPlatform` e apresenta duas áreas:

- **Notas recebidas:** consulta o serviço oficial `NFeDistribuicaoDFe` do Ambiente Nacional usando certificado digital com chave privada.
- **Notas emitidas:** procura arquivos XML de NF-e já existentes no computador e filtra pelo CNPJ do emitente. A distribuição de DF-e não fornece ao emitente o XML da própria NF-e por este serviço.

## Notas recebidas pela SEFAZ

- Ambientes disponíveis: produção e homologação.
- O CNPJ informado deve ter 14 dígitos e pertencer à mesma base de CNPJ do certificado.
- A comunicação HTTPS usa autenticação mútua com o certificado selecionado no repositório pessoal do usuário.
- O AssinaPDF envia consultas `distNSU`, descompacta os `docZip` GZip e salva os documentos localmente.
- O NSU é persistido por CNPJ e ambiente em `%LOCALAPPDATA%/FutureSolucoes/AssinaPDF/Sefaz` (Windows) ou no diretório equivalente de dados locais do usuário.
- Os XMLs e resumos são salvos por padrão em `Documentos/AssinaPDF/XML/Recebidas/{CNPJ}`. O usuário pode escolher outra pasta-base.
- A consulta processa no máximo 20 lotes por execução e preserva o último NSU para continuar depois.
- Após retorno sem documentos (`cStat=137`), consumo indevido (`cStat=656`) ou quando o NSU alcança o máximo disponível, o aplicativo bloqueia novas consultas por uma hora para respeitar as regras do serviço.

### Limites legais e operacionais

A SEFAZ pode entregar inicialmente somente o resumo da NF-e (`resNFe`). O XML completo do destinatário depende de manifestação registrada conforme as regras fiscais. Esta versão **não envia manifestação automaticamente** e não confirma, desconhece nem declara operação não realizada em nome do usuário. A decisão e o registro da manifestação permanecem sob responsabilidade do contribuinte.

O serviço de distribuição não disponibiliza ao emitente a própria NF-e. Por isso, a área de notas emitidas faz busca local e não promete download dessas notas diretamente da SEFAZ.

## Notas emitidas — busca local

- Pesquisa recursivamente nas pastas Documentos, Área de Trabalho e Downloads.
- Permite selecionar uma pasta adicional.
- Limite de 3.000 arquivos XML por pesquisa e profundidade máxima de 10 níveis.
- Considera NF-e processada modelo 55, versão 4.00, autorizada e compatível com o leitor fiscal existente.
- O CNPJ do emitente do XML precisa corresponder ao CNPJ informado.

## Privacidade

O certificado, a chave privada, o NSU e os XMLs são utilizados e armazenados localmente. Esta funcionalidade não envia certificados ou arquivos fiscais para o Supabase ou para servidores da Future.

## Teste local

No Windows, a partir da raiz do repositório:

```powershell
dotnet test .\tests\AssinaPDF.CrossPlatform.Tests\AssinaPDF.CrossPlatform.Tests.csproj -c Release
dotnet run --project .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj
```

Recomenda-se validar primeiro com o ambiente **Homologação** e um certificado de teste autorizado. O teste real de produção depende de certificado válido, CNPJ compatível, conectividade com o Ambiente Nacional e documentos efetivamente distribuídos para aquele ator.
