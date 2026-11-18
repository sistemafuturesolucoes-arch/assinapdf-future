import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "npm:@supabase/supabase-js@2.98.0";

const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const supabase = createClient(supabaseUrl, serviceKey, { auth: { persistSession: false, autoRefreshToken: false } });

function json(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

function safeEqual(a: string, b: string) {
  const aa = new TextEncoder().encode(a);
  const bb = new TextEncoder().encode(b);
  if (aa.length !== bb.length) return false;
  let diff = 0;
  for (let i = 0; i < aa.length; i++) diff |= aa[i] ^ bb[i];
  return diff === 0;
}

function asaasBaseUrl() {
  return Deno.env.get("ASAAS_ENVIRONMENT")?.toLowerCase() === "production"
    ? "https://api.asaas.com"
    : "https://api-sandbox.asaas.com";
}

async function asaasRequest(path: string, method: string, body?: unknown) {
  const apiKey = Deno.env.get("ASAAS_API_KEY");
  if (!apiKey) throw new Error("ASAAS_API_KEY não configurada.");
  const response = await fetch(asaasBaseUrl() + path, {
    method,
    headers: {
      "Content-Type": "application/json",
      "access_token": apiKey,
      "User-Agent": "AssinaPDF-Future/1.0",
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    console.error("[assinapdf-asaas-api]", response.status, payload);
    throw new Error("O Asaas não conseguiu criar a cobrança. Confira a configuração e tente novamente.");
  }
  return payload;
}

async function authenticateUser(request: Request) {
  const authorization = request.headers.get("Authorization") || "";
  const token = authorization.replace(/^Bearer\s+/i, "").trim();
  if (!token) return null;
  const { data, error } = await supabase.auth.getUser(token);
  return error ? null : data.user;
}

async function createMonthlyCheckout(request: Request, body: any) {
  const user = await authenticateUser(request);
  if (!user?.id || !user.email) return json({ error: "Entre na sua conta AssinaPDF para contratar o Premium." }, 401);

  const apiKey = Deno.env.get("ASAAS_API_KEY");
  if (!apiKey) return json({ error: "A contratação online ainda está em configuração. Entre em contato com a Future Soluções ou utilize sua chave manual." }, 503);

  const { error: customerError } = await supabase.from("assinapdf_customers").upsert({
    user_id: user.id,
    email: String(user.email).toLowerCase(),
    full_name: String(user.user_metadata?.full_name || user.user_metadata?.name || ""),
    updated_at: new Date().toISOString(),
  }, { onConflict: "user_id" });
  if (customerError) throw customerError;

  const { data: plan, error: planError } = await supabase.from("assinapdf_plans")
    .select("id,code,name,price_cents,billing_interval,is_active")
    .eq("code", "premium").eq("is_active", true).single();
  if (planError || !plan || plan.billing_interval !== "monthly")
    return json({ error: "O plano Premium mensal não está disponível." }, 409);
  if (!Number.isSafeInteger(Number(plan.price_cents)) || Number(plan.price_cents) <= 0)
    return json({ error: "O administrador ainda precisa definir o preço mensal do Premium no CRM." }, 409);

  const { data: licenses } = await supabase.from("assinapdf_licenses")
    .select("id,status,valid_until,assinapdf_plans(code)")
    .eq("user_id", user.id).eq("status", "active")
    .order("created_at", { ascending: false }).limit(20);
  const hasActivePremium = (licenses || []).some((license: any) =>
    license.assinapdf_plans?.code === "premium" &&
    (!license.valid_until || new Date(license.valid_until).getTime() > Date.now())
  );
  if (hasActivePremium) return json({ error: "Sua conta já possui uma licença Premium ativa." }, 409);

  const { data: paidManualKey } = await supabase.from("assinapdf_activation_keys")
    .select("id").eq("status", "available")
    .eq("customer_email", String(user.email).toLowerCase())
    .limit(1).maybeSingle();
  if (paidManualKey) return json({ error: "Já existe uma chave manual paga vinculada ao seu e-mail. Ative essa chave em vez de contratar novamente." }, 409);

  const { data: existingRecurring } = await supabase.from("assinapdf_subscriptions")
    .select("id,status,external_subscription_id")
    .eq("user_id", user.id).eq("provider", "asaas")
    .in("status", ["active", "past_due"]).limit(1).maybeSingle();
  if (existingRecurring) {
    return json({ error: existingRecurring.status === "active"
      ? "Sua conta já possui uma assinatura Premium ativa."
      : "Existe uma cobrança recorrente em atraso. Regularize a cobrança atual ou fale com a Future Soluções antes de criar outra assinatura." }, 409);
  }

  const { data: existing } = await supabase.from("assinapdf_subscriptions")
    .select("id,checkout_url,external_payment_link_id,status")
    .eq("user_id", user.id).eq("provider", "asaas").eq("status", "pending")
    .order("created_at", { ascending: false }).limit(1).maybeSingle();
  if (existing?.checkout_url && existing.external_payment_link_id) {
    return json({ checkout_url: existing.checkout_url, reused: true, billing_interval: "monthly" });
  }

  const link = await asaasRequest("/v3/paymentLinks", "POST", {
    name: "AssinaPDF Premium",
    description: "Assinatura mensal recorrente do AssinaPDF Premium",
    value: Number(plan.price_cents) / 100,
    billingType: "UNDEFINED",
    chargeType: "RECURRENT",
    subscriptionCycle: "MONTHLY",
    dueDateLimitDays: 5,
    externalReference: user.id,
    notificationEnabled: true,
    limit: 1,
  });

  if (!link?.id || !link?.url) throw new Error("O Asaas não retornou o link de pagamento esperado.");
  const record = {
    user_id: user.id,
    plan_id: plan.id,
    status: "pending",
    provider: "asaas",
    external_payment_link_id: String(link.id),
    checkout_url: String(link.url),
    current_period_start: new Date().toISOString(),
    cancel_at_period_end: false,
    updated_at: new Date().toISOString(),
  };
  let saveError;
  if (existing?.id) {
    const result = await supabase.from("assinapdf_subscriptions").update(record).eq("id", existing.id);
    saveError = result.error;
  } else {
    const result = await supabase.from("assinapdf_subscriptions").insert(record);
    saveError = result.error;
  }
  if (saveError) throw saveError;

  return json({ checkout_url: link.url, reused: false, billing_interval: "monthly" });
}

async function findSubscription(payment: any, subscriptionObject: any) {
  const linkId = String(payment?.paymentLink || subscriptionObject?.paymentLink || "");
  if (linkId) {
    const { data } = await supabase.from("assinapdf_subscriptions").select("*")
      .eq("external_payment_link_id", linkId).maybeSingle();
    if (data) return data;
  }

  const externalSubscriptionId = String(payment?.subscription || subscriptionObject?.id || "");
  if (externalSubscriptionId) {
    const { data } = await supabase.from("assinapdf_subscriptions").select("*")
      .eq("external_subscription_id", externalSubscriptionId).maybeSingle();
    if (data) return data;
  }

  const externalReference = String(payment?.externalReference || subscriptionObject?.externalReference || "");
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(externalReference)) {
    const { data } = await supabase.from("assinapdf_subscriptions").select("*")
      .eq("user_id", externalReference).eq("provider", "asaas")
      .order("created_at", { ascending: false }).limit(1).maybeSingle();
    if (data) return data;
  }
  return null;
}

async function receiveWebhook(request: Request) {
  const configuredToken = Deno.env.get("ASAAS_WEBHOOK_TOKEN");
  if (!configuredToken) return json({ error: "Webhook Asaas ainda não configurado." }, 503);
  const receivedToken = request.headers.get("asaas-access-token") || "";
  if (!safeEqual(receivedToken, configuredToken)) return json({ error: "Não autorizado." }, 401);

  const body = await request.json().catch(() => null);
  const eventId = String(body?.id || "");
  const eventType = String(body?.event || "");
  if (!eventId || !eventType) return json({ error: "Evento inválido." }, 400);

  const payment = body?.payment || null;
  const subscriptionObject = body?.subscription || null;
  const externalPaymentId = payment?.id ? String(payment.id) : null;

  const { data: inserted, error: insertError } = await supabase.from("assinapdf_payment_events")
    .upsert({
      provider: "asaas",
      external_event_id: eventId,
      external_payment_id: externalPaymentId,
      event_type: eventType,
      payload: body,
    }, { onConflict: "provider,external_event_id", ignoreDuplicates: true })
    .select("id,processed_at").maybeSingle();
  if (insertError) throw insertError;

  let eventRow = inserted;
  if (!eventRow) {
    const { data: existingEvent, error } = await supabase.from("assinapdf_payment_events")
      .select("id,processed_at").eq("provider", "asaas").eq("external_event_id", eventId).single();
    if (error) throw error;
    if (existingEvent.processed_at) return json({ received: true, duplicate: true });
    eventRow = existingEvent;
  }

  if (eventType === "SUBSCRIPTION_CREATED" && subscriptionObject?.id) {
    const mapped = await findSubscription(null, subscriptionObject);
    if (mapped) {
    const { error } = await supabase.from("assinapdf_subscriptions")
      .update({
        external_subscription_id: String(subscriptionObject.id),
        external_customer_id: subscriptionObject.customer ? String(subscriptionObject.customer) : null,
        updated_at: new Date().toISOString(),
      }).eq("id", mapped.id);
    if (error) throw error;
    }
  }

  const handledPaymentEvents = ["PAYMENT_CREATED", "PAYMENT_UPDATED", "PAYMENT_CONFIRMED", "PAYMENT_RECEIVED", "PAYMENT_OVERDUE", "PAYMENT_REFUNDED", "PAYMENT_PARTIALLY_REFUNDED", "PAYMENT_RECEIVED_IN_CASH_UNDONE", "PAYMENT_CHARGEBACK_REQUESTED", "PAYMENT_CHARGEBACK_DISPUTE", "PAYMENT_DELETED", "PAYMENT_RESTORED"];
  if (payment?.id && handledPaymentEvents.includes(eventType)) {
    const mapped = await findSubscription(payment, null);
    if (!mapped) return json({ error: "Cobrança recebida, mas ainda não foi possível associá-la à conta AssinaPDF." }, 409);

    const confirmedEvents = ["PAYMENT_CONFIRMED", "PAYMENT_RECEIVED"];
    const status = confirmedEvents.includes(eventType) ? "confirmed"
      : eventType === "PAYMENT_OVERDUE" ? "overdue"
      : ["PAYMENT_REFUNDED", "PAYMENT_PARTIALLY_REFUNDED", "PAYMENT_RECEIVED_IN_CASH_UNDONE", "PAYMENT_CHARGEBACK_REQUESTED", "PAYMENT_CHARGEBACK_DISPUTE"].includes(eventType) ? "refunded"
      : eventType === "PAYMENT_DELETED" ? "canceled"
      : "pending";

    const paidAtRaw = payment.paymentDate || payment.clientPaymentDate || payment.confirmedDate || null;
    const paidAt = paidAtRaw && !Number.isNaN(Date.parse(paidAtRaw)) ? new Date(paidAtRaw).toISOString() : null;
    const dueDate = payment.dueDate && /^\d{4}-\d{2}-\d{2}$/.test(payment.dueDate) ? payment.dueDate : null;
    const amountCents = Math.max(0, Math.round(Number(payment.value || 0) * 100));

    const { error } = await supabase.rpc("apply_assinapdf_asaas_payment", {
      p_subscription_id: mapped.id,
      p_external_subscription_id: payment.subscription ? String(payment.subscription) : null,
      p_external_payment_id: String(payment.id),
      p_amount_cents: amountCents,
      p_status: status,
      p_due_date: dueDate,
      p_paid_at: paidAt,
      p_invoice_url: payment.invoiceUrl ? String(payment.invoiceUrl) : null,
      p_description: payment.description ? String(payment.description) : "AssinaPDF Premium mensal",
      p_payload: body,
    });
    if (error) throw error;
  }

  if (["SUBSCRIPTION_DELETED", "SUBSCRIPTION_INACTIVATED"].includes(eventType) && subscriptionObject?.id) {
    const mapped = await findSubscription(null, subscriptionObject);
    if (mapped) {
      const { error } = await supabase.from("assinapdf_subscriptions")
        .update({ status: "canceled", cancel_at_period_end: true, updated_at: new Date().toISOString() })
        .eq("id", mapped.id);
      if (error) throw error;
    }
  }

  const { error: markError } = await supabase.from("assinapdf_payment_events")
    .update({ processed_at: new Date().toISOString() }).eq("id", eventRow.id);
  if (markError) throw markError;

  return json({ received: true, event: eventType });
}

Deno.serve(async (request: Request) => {
  if (request.method === "OPTIONS") return new Response("ok");
  if (request.method !== "POST") return json({ error: "Método não permitido." }, 405);

  const url = new URL(request.url);
  try {
    if (url.searchParams.get("mode") === "webhook" || request.headers.has("asaas-access-token"))
      return await receiveWebhook(request);

    const body = await request.json().catch(() => ({}));
    if (body?.action === "create_monthly_checkout")
      return await createMonthlyCheckout(request, body);
    return json({ error: "Ação não reconhecida." }, 400);
  } catch (error) {
    console.error("[assinapdf-asaas]", error);
    return json({ error: "Não foi possível processar a operação Asaas." }, 500);
  }
});