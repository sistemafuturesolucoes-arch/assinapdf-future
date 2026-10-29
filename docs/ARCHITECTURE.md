# Arquitetura do AssinaPDF

## Princípios

1. O AssinaPDF é um aplicativo desktop independente, hospedado neste repositório por conveniência.
2. Código, dependências, build e artefatos ficam sob `apps/assinapdf-desktop`; artefatos gerados não devem ser commitados.
3. O CRM web não deve importar assemblies ou dependências do aplicativo desktop.
4. Documentos são abertos e assinados localmente. Não enviar PDFs a servidores sem ação explícita e informada do usuário.
5. Chaves privadas não devem ser exportadas nem transmitidas.
6. A interface nunca deve confundir imagem de assinatura com assinatura digital criptográfica.
7. O original é preservado: a assinatura gera um novo arquivo por padrão.

## Componentes atuais

- **Desktop UI:** Windows Forms.
- **PDF viewer:** Microsoft WebView2 hospedando o visualizador PDF do Microsoft Edge.
- **Certificate discovery:** repositório Pessoal do usuário atual do Windows (`CurrentUser/My`); a interface mostra metadados e disponibilidade local da chave privada.
- **PAdES signing:** `SimpleSign.PAdES` 0.9.2, perfil básico Baseline-B-B, usando o certificado escolhido sem exportar a chave privada.
- **CI:** workflow dedicado que compila e publica um artefato de teste, sem implantar o CRM.

## Limites e próximas etapas

- A seleção atual verifica a validade temporal local e a presença da chave privada, mas não é uma política completa de elegibilidade ICP-Brasil.
- O perfil básico não acrescenta carimbo do tempo nem evidência de revogação de longo prazo.
- É necessário validar o arquivo assinado com ferramenta independente e confirmar integridade, cadeia, revogação e timestamp como resultados separados.
- Testar certificados A1 instalados no Windows, chaves não exportáveis, tokens/cartões e diferentes provedores CryptoAPI/CNG.
- Adicionar testes automatizados, matriz de documentos, instalador x64 e verificação do WebView2 Runtime.

## Decisões técnicas e limites

O WebView2 depende do runtime Microsoft Edge e de sua implementação do visualizador PDF; a versão final deve verificar o comportamento em máquinas limpas e em PDFs protegidos, grandes ou corrompidos. A biblioteca PAdES é recente e está fixada em versão específica; revisão de dependência e testes de interoperabilidade são bloqueadores de distribuição. Não afirmar conformidade ICP-Brasil apenas por produzir um PDF assinado: são necessários testes técnicos e validação aplicável.
