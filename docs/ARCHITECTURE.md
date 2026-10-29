# Arquitetura do AssinaPDF

## Princípios

1. O AssinaPDF é um aplicativo desktop independente, hospedado neste repositório por conveniência.
2. Código, dependências, build e artefatos ficam sob `apps/assinapdf-desktop`; artefatos gerados não devem ser commitados.
3. O CRM web não deve importar assemblies ou dependências do aplicativo desktop.
4. Documentos são abertos localmente. Não enviar PDFs a servidores sem ação explícita e informada do usuário.
5. Chaves privadas não devem ser exportadas nem transmitidas.
6. A interface nunca deve confundir imagem de assinatura com assinatura digital criptográfica.

## Componentes atuais

- **Desktop UI:** Windows Forms.
- **PDF viewer:** Microsoft WebView2 hospedando o visualizador PDF do Microsoft Edge.
- **Certificate discovery:** repositório Pessoal do usuário atual do Windows (`CurrentUser/My`); apenas metadados são mostrados.
- **CI:** workflow dedicado que compila e publica um artefato de teste, sem implantar o CRM.

## Componentes pendentes

- **Signing engine:** biblioteca PAdES com licença e condições de redistribuição aprovadas.
- **Certificate eligibility:** validação adequada de usos de chave, cadeia, revogação, timestamp e dispositivos criptográficos.
- **Installer:** instalador x64 com verificação do WebView2 Runtime, identidade de produto e desinstalação limpa.
- **Tests:** testes automatizados e matriz de documentos/certificados.

## Decisões técnicas e limites

O WebView2 depende do runtime Microsoft Edge e de sua implementação do visualizador PDF; a versão final deve verificar o comportamento em máquinas limpas e em PDFs protegidos, grandes ou corrompidos. A listagem de certificados é informativa, não uma declaração de confiança. Não afirmar conformidade ICP-Brasil apenas por produzir um PDF assinado: são necessários testes técnicos e validação aplicável.
