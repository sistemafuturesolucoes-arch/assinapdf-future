# AssinaPDF — configuração do Asaas

A integração está preparada para assinatura Premium mensal recorrente, usando checkout hospedado pelo Asaas. O aplicativo nunca recebe nem armazena dados de cartão.

## 1. Segredos no Supabase

No projeto `zcquhikpsqohrclflnjo`, abra **Project Settings → Edge Functions → Secrets** e cadastre:

| Nome | Valor |
|---|---|
| `ASAAS_API_KEY` | Chave de API do Asaas. Começar pela chave do Sandbox. |
| `ASAAS_WEBHOOK_TOKEN` | Token aleatório forte, com pelo menos 32 caracteres. |
| `ASAAS_ENVIRONMENT` | `sandbox` durante homologação; mudar para `production` somente após aprovação dos testes. |

Nunca coloque a chave de API do Asaas no código do aplicativo, no CRM, em variáveis `VITE_*` ou no Git.

## 2. Webhook no Asaas

No painel Asaas do mesmo ambiente da chave de API, cadastre um webhook:

- URL: `https://zcquhikpsqohrclflnjo.supabase.co/functions/v1/assinapdf-asaas?mode=webhook`
- Token de autenticação: exatamente o valor de `ASAAS_WEBHOOK_TOKEN`.
- Habilitado: sim.
- Eventos:
  - `SUBSCRIPTION_CREATED`
  - `SUBSCRIPTION_UPDATED`
  - `SUBSCRIPTION_DELETED`
  - `SUBSCRIPTION_INACTIVATED`
  - `PAYMENT_CREATED`
  - `PAYMENT_CONFIRMED`
  - `PAYMENT_RECEIVED`
  - `PAYMENT_OVERDUE`
  - `PAYMENT_REFUNDED`
  - `PAYMENT_DELETED`

O endpoint confere o token do cabeçalho `asaas-access-token`, registra os eventos e trata duplicidades usando o identificador do evento. Não use a página de retorno do checkout como confirmação de pagamento; a liberação acontece pelo webhook.

## 3. Preço do Premium

No CRM Vision, abra Administração do AssinaPDF → Planos → Premium. Defina o preço mensal e mantenha a periodicidade como **Mensal recorrente**. O checkout não será criado se o preço estiver vazio ou zerado.

## 4. Testes obrigatórios no Sandbox

1. Criar uma conta no AssinaPDF.
2. Solicitar a assinatura mensal pelo botão da tela Minha conta.
3. Concluir o checkout no ambiente Sandbox.
4. Confirmar que `SUBSCRIPTION_CREATED` e o evento de pagamento chegaram ao Supabase.
5. Confirmar que a licença foi criada/renovada e que o CRM mostra o pagamento.
6. Simular atraso, pagamento posterior, estorno e cancelamento.
7. Reenviar um evento e confirmar que ele não prorroga a licença duas vezes.

Somente depois da homologação deve ser cadastrada a chave de produção, alterado `ASAAS_ENVIRONMENT` para `production` e configurado o webhook no painel de produção.

## 5. Fluxo manual por WhatsApp

O administrador seleciona Premium, define a validade exata, registra o cliente e confirma o recebimento do pagamento. O CRM exibe a chave completa uma única vez. O cliente cria sua conta e resgata a chave no aplicativo. O banco mantém apenas o hash da chave.
