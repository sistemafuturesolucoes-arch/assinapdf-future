# Segurança — AssinaPDF

## Requisitos obrigatórios

- Chaves privadas permanecem no dispositivo ou hardware criptográfico do usuário.
- Nunca registrar senhas de certificados, PINs, conteúdo integral de PDFs ou material de chave privada.
- Não enviar documentos a servidores sem uma ação explícita e informada do usuário.
- Tratar PDFs como entrada não confiável; limitar erros, recursos e operações de arquivo.
- Salvar o PDF assinado como novo arquivo por padrão e preservar o original.
- Não declarar uma assinatura confiável apenas porque o processo de assinatura terminou.
- Separar os resultados de integridade criptográfica, cadeia de confiança, revogação e carimbo do tempo.
- Manter dependências atualizadas e revisar licenças e avisos de segurança antes da distribuição.

## Estado atual

A assinatura PAdES básica foi conectada ao fluxo desktop, usando certificado selecionado do repositório Pessoal do Windows. Isso ainda não substitui testes com certificados de produção, tokens/cartões, ferramentas de validação independentes nem revisão de segurança. Não distribuir como versão final até os critérios de liberação no roadmap serem concluídos.

## Reporte de vulnerabilidades

Não publique detalhes exploráveis de uma vulnerabilidade em uma issue pública. Entre em contato com a equipe responsável pelo repositório por um canal privado disponível na página do projeto.
