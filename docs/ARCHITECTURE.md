# Arquitetura inicial

## Princípios

1. O AssinaPDF é um aplicativo desktop independente, hospedado neste repositório apenas por conveniência.
2. O código-fonte, dependências, build e artefatos ficam sob `apps/assinapdf-desktop` (artefatos gerados não devem ser commitados).
3. O CRM web não deve importar assemblies, scripts de build ou dependências do aplicativo desktop.
4. A assinatura deve ocorrer localmente no computador do usuário; chaves privadas não devem ser enviadas ao CRM ou a serviços remotos.
5. A interface nunca deve confundir imagem de assinatura com assinatura digital criptográfica.

## Componentes planejados

- **Desktop UI:** Windows Forms para a primeira base Windows.
- **PDF viewer:** decisão pendente até avaliar renderização, acessibilidade, licenças e distribuição offline.
- **Certificate provider:** enumerar certificados elegíveis do repositório de certificados do usuário Windows, sem exportar chaves privadas.
- **Signing engine:** módulo isolado para assinatura PAdES, com validação de integridade após assinar.
- **Installer:** instalador Windows separado, com identidade de produto e desinstalação limpa.
- **CI:** workflow dedicado; não publicar nem implantar o CRM a partir deste workflow.

## Decisões ainda não fechadas

Antes de adicionar bibliotecas, documentar a licença, versão, manutenção, vulnerabilidades conhecidas e condições de redistribuição. Não afirmar conformidade ICP-Brasil apenas por gerar um PDF assinado: a implementação precisa passar por testes técnicos e validação aplicável.
