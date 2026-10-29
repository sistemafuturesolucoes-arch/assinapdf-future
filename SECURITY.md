# Segurança — AssinaPDF

## Requisitos obrigatórios

- Chaves privadas permanecem no dispositivo ou hardware criptográfico do usuário.
- Nunca registrar senhas de certificados, PINs, conteúdo integral de PDFs ou material de chave privada.
- Não enviar documentos a servidores sem uma ação explícita e informada do usuário.
- Tratar PDFs como entrada não confiável; limitar erros, recursos e operações de arquivo.
- Validar a integridade do arquivo assinado e manter o original intacto por padrão.
- Mostrar mensagens claras quando uma assinatura não puder ser validada; não declarar que está válida apenas porque a operação terminou.
- Manter dependências atualizadas e revisar licenças e avisos de segurança antes da distribuição.

## Reporte de vulnerabilidades

Não publique detalhes exploráveis de uma vulnerabilidade em uma issue pública. Entre em contato com a equipe responsável pelo repositório por um canal privado disponível na página do projeto.

## Limites atuais

Esta é uma especificação inicial, não uma auditoria de segurança. O protótipo atual ainda não abre/renderiza nem assina o conteúdo de PDFs.
