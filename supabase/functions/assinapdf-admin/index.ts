import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "npm:@supabase/supabase-js@2.98.0";

const CRM_URL = "https://jhmdavcidwqycjedvgoz.supabase.co";
const CRM_PUBLISHABLE_KEY = "sb_publishable_Gs_F7k841mI5SzTYFkSbzQ_Kgfz0GHu";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const supabase = createClient(SUPABASE_URL, SERVICE_ROLE_KEY, {
  auth: { persistSession: false, autoRefreshToken: false },
});

const allowedOrigins = new Set([
  "https://crm.futuresolucoes.com.br",
  "https://spc-vision-crm.vercel.app",
  "http://localhost:5173",
]);

function corsHeaders(origin: string | null) {
  const isTrustedVercelPreview = !!origin
    && origin.startsWith("https://spc-vision-crm-")
    && origin.endsWith(".vercel.app");
  const allowedOrigin = origin && (allowedOrigins.has(origin) || isTrustedVercelPreview)
    ? origin
    : "https://crm.futuresolucoes.com.br";
  return {
    "Access-Control-Allow-Origin": allowedOrigin,
    "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Access-Control-Max-Age": "86400",
    "Vary": "Origin",
  };
}

function json(data: unknown, status = 200, origin: string | null = null) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { ...corsHeaders(origin), "Content-Type": "application/json; charset=utf-8" },
  });
}

async function authenticateAdmin(request: Request) {
  const authorization = request.headers.get("Authorization") || "";
  const token = authorization.replace(/^Bearer\s+/i, "").trim();
  if (!token) return { error: "Sessão do CRM não informada.", status: 401 } as const;

  const response = await fetch(`${CRM_URL}/auth/v1/user`, {
    headers: { apikey: CRM_PUBLISHABLE_KEY, Authorization: `Bearer ${token}` },
  });
  if (!response.ok) return { error: "Sessão do CRM inválida ou expirada.", status: 401 } as const;

  const crmUser = await response.json();
  if (!crmUser?.id || !crmUser?.email) return { error: "Não foi possível validar o usuário do CRM.", status: 401 } as const;

  const { data: admin, error } = await supabase
    .from("assinapdf_admin_users")
    .select("crm_user_id,email,admin_role,is_active")
    .eq("crm_user_id", crmUser.id)
    .eq("email", String(crmUser.email).toLowerCase())
    .eq("is_active", true)
    .maybeSingle();

  if (error) return { error: "Falha ao consultar as permissões administrativas.", status: 500 } as const;
  if (!admin) return { error: "Seu usuário não está autorizado a administrar o AssinaPDF.", status: 403 } as const;
  return { user: crmUser, admin } as const;
}

async function audit(user: any, action: string, targetType?: string, targetId?: string, metadata: Record<string, unknown> = {}) {
  await supabase.from("assinapdf_admin_audit").insert({
    crm_user_id: user.id,
    actor_email: String(user.email).toLowerCase(),
    action,
    target_type: targetType ?? null,
    target_id: targetId ?? null,
    metadata,
  });
}

Deno.serve(async (request: Request) => {
  const origin = request.headers.get("Origin");
  if (request.method === "OPTIONS") return new Response("ok", { headers: corsHeaders(origin) });
  if (request.method !== "POST") return json({ error: "Método não permitido." }, 405, origin);

  try {
    const auth = await authenticateAdmin(request);
    if ("error" in auth) return json({ error: auth.error }, auth.status, origin);

    const body = await request.json().catch(() => ({}));
    const action = String(body?.action || "");
    const isOwner = auth.admin.admin_role === "owner";

    if (action === "overview") {
      const [customers, licenses, subscriptions, releases] = await Promise.all([
        supabase.from("assinapdf_customers").select("user_id", { count: "exact", head: true }),
        supabase.from("assinapdf_licenses").select("id", { count: "exact", head: true }).eq("status", "active"),
        supabase.from("assinapdf_subscriptions").select("id", { count: "exact", head: true }).eq("status", "active"),
        supabase.from("assinapdf_releases").select("id", { count: "exact", head: true }).eq("is_published", true),
      ]);
      const firstError = customers.error || licenses.error || subscriptions.error || releases.error;
      if (firstError) throw firstError;
      return json({
        customers: customers.count || 0,
        activeLicenses: licenses.count || 0,
        activeSubscriptions: subscriptions.count || 0,
        publishedReleases: releases.count || 0,
      }, 200, origin);
    }

    if (action === "list_plans") {
      const { data, error } = await supabase.from("assinapdf_plans")
        .select("*,assinapdf_plan_features(*)").order("sort_order");
      if (error) throw error;
      return json({ plans: data || [] }, 200, origin);
    }

    if (action === "save_plan") {
      const input = body.plan || {};
      const code = String(input.code || "").trim().toLowerCase();
      const name = String(input.name || "").trim();
      if (!/^[a-z0-9_-]{2,40}$/.test(code) || !name) return json({ error: "Informe um código válido e o nome do plano." }, 400, origin);
      const price = input.price_cents === null || input.price_cents === "" ? null : Number(input.price_cents);
      if (price !== null && (!Number.isSafeInteger(price) || price < 0)) return json({ error: "Preço inválido." }, 400, origin);
      const record = {
        ...(input.id ? { id: input.id } : {}),
        code, name,
        description: input.description ? String(input.description) : null,
        price_cents: price,
        billing_interval: input.billing_interval || null,
        device_limit: Math.max(1, Number(input.device_limit) || 1),
        is_active: input.is_active !== false,
        sort_order: Number(input.sort_order) || 0,
        updated_at: new Date().toISOString(),
      };
      const planQuery = input.id
        ? supabase.from("assinapdf_plans").update(record).eq("id", input.id)
        : supabase.from("assinapdf_plans").insert(record);
      const { data, error } = await planQuery.select().single();
      if (error) throw error;
      if (Array.isArray(input.features)) {
        const features = input.features.map((feature: any) => ({
          plan_id: data.id,
          feature_key: String(feature.feature_key || "").trim(),
          is_enabled: feature.is_enabled !== false,
          numeric_limit: feature.numeric_limit === "" ? null : feature.numeric_limit ?? null,
          config: feature.config && typeof feature.config === "object" ? feature.config : {},
          updated_at: new Date().toISOString(),
        })).filter((feature: any) => feature.feature_key);
        if (features.length) {
          const { error: featureError } = await supabase.from("assinapdf_plan_features").upsert(features, { onConflict: "plan_id,feature_key" });
          if (featureError) throw featureError;
        }
      }
      await audit(auth.user, "plan.saved", "plan", data.id, { code });
      return json({ plan: data }, 200, origin);
    }

    if (action === "list_customers") {
      const [customers, licenses, subscriptions] = await Promise.all([
        supabase.from("assinapdf_customers").select("*").order("created_at", { ascending: false }).limit(500),
        supabase.from("assinapdf_licenses").select("*,assinapdf_plans(name,code)").order("created_at", { ascending: false }).limit(500),
        supabase.from("assinapdf_subscriptions").select("*,assinapdf_plans(name,code)").order("created_at", { ascending: false }).limit(500),
      ]);
      const error = customers.error || licenses.error || subscriptions.error;
      if (error) throw error;
      return json({ customers: customers.data || [], licenses: licenses.data || [], subscriptions: subscriptions.data || [] }, 200, origin);
    }

    if (action === "assign_license") {
      const input = body.license || {};
      if (!input.user_id || !input.plan_id) return json({ error: "Selecione o cliente e o plano." }, 400, origin);
      const { data: plan, error: planError } = await supabase.from("assinapdf_plans").select("id,code,device_limit,is_active").eq("id", input.plan_id).single();
      if (planError || !plan?.is_active) return json({ error: "Plano não encontrado ou inativo." }, 400, origin);
      const { data, error } = await supabase.from("assinapdf_licenses").insert({
        user_id: input.user_id,
        plan_id: plan.id,
        status: input.status || (plan.code === "free" ? "active" : "pending"),
        max_devices: Math.max(1, Number(input.max_devices) || plan.device_limit),
        valid_until: input.valid_until || null,
      }).select().single();
      if (error) throw error;
      await audit(auth.user, "license.created", "license", data.id, { user_id: input.user_id, plan_code: plan.code });
      return json({ license: data }, 200, origin);
    }

    if (action === "set_license_status") {
      const { id, status } = body.license || {};
      if (!id || !["pending", "active", "suspended", "revoked", "expired"].includes(status)) return json({ error: "Status de licença inválido." }, 400, origin);
      const { data, error } = await supabase.from("assinapdf_licenses").update({ status, updated_at: new Date().toISOString() }).eq("id", id).select().single();
      if (error) throw error;
      await audit(auth.user, "license.status_changed", "license", id, { status });
      return json({ license: data }, 200, origin);
    }

    if (action === "list_releases") {
      const { data, error } = await supabase.from("assinapdf_releases").select("*").order("created_at", { ascending: false });
      if (error) throw error;
      return json({ releases: data || [] }, 200, origin);
    }

    if (action === "save_release") {
      const input = body.release || {};
      const platform = String(input.platform || "");
      const channel = String(input.channel || "stable");
      const version = String(input.version || "").trim();
      const downloadUrl = String(input.download_url || "").trim();
      const sha256 = String(input.sha256 || "").trim().toLowerCase();
      if (!["windows-x64", "macos-x64", "macos-arm64", "macos-universal"].includes(platform)) return json({ error: "Plataforma inválida." }, 400, origin);
      if (!["stable", "beta"].includes(channel) || !version) return json({ error: "Informe canal e versão válidos." }, 400, origin);
      if (!downloadUrl.startsWith("https://")) return json({ error: "O link de download precisa usar HTTPS." }, 400, origin);
      if (!/^[a-f0-9]{64}$/.test(sha256)) return json({ error: "Informe o SHA-256 com 64 caracteres hexadecimais." }, 400, origin);
      const record = {
        platform, channel, version, download_url: downloadUrl, sha256,
        build_number: Math.max(1, Number(input.build_number) || 1),
        minimum_supported_version: input.minimum_supported_version || null,
        signature: input.signature || null,
        release_notes: input.release_notes || null,
        is_mandatory: input.is_mandatory === true,
        is_published: input.is_published === true,
        published_at: input.is_published === true ? new Date().toISOString() : null,
      };
      const { data, error } = await supabase.from("assinapdf_releases").upsert(record, { onConflict: "platform,channel,version" }).select().single();
      if (error) throw error;
      await audit(auth.user, "release.saved", "release", data.id, { platform, channel, version, published: data.is_published });
      return json({ release: data }, 200, origin);
    }

    if (action === "list_admins") {
      if (!isOwner) return json({ error: "Somente o administrador principal pode gerenciar administradores." }, 403, origin);
      const { data, error } = await supabase.from("assinapdf_admin_users").select("*").order("created_at");
      if (error) throw error;
      return json({ admins: data || [] }, 200, origin);
    }

    if (action === "save_admin") {
      if (!isOwner) return json({ error: "Somente o administrador principal pode gerenciar administradores." }, 403, origin);
      const input = body.admin || {};
      const crmUserId = String(input.crm_user_id || "");
      const email = String(input.email || "").trim().toLowerCase();
      if (!crmUserId || !email || !["owner", "gestor"].includes(input.admin_role)) return json({ error: "Dados do administrador inválidos." }, 400, origin);
      const { data, error } = await supabase.from("assinapdf_admin_users").upsert({
        crm_user_id: crmUserId, email, admin_role: input.admin_role,
        is_active: input.is_active !== false, updated_at: new Date().toISOString(),
      }, { onConflict: "crm_user_id" }).select().single();
      if (error) throw error;
      await audit(auth.user, "admin.saved", "admin", crmUserId, { email, admin_role: input.admin_role });
      return json({ admin: data }, 200, origin);
    }

    if (action === "list_audit") {
      const { data, error } = await supabase.from("assinapdf_admin_audit").select("*").order("created_at", { ascending: false }).limit(200);
      if (error) throw error;
      return json({ audit: data || [] }, 200, origin);
    }

    return json({ error: "Ação não reconhecida." }, 400, origin);
  } catch (error) {
    console.error("[assinapdf-admin]", error);
    return json({ error: "Não foi possível concluir a operação administrativa." }, 500, origin);
  }
});
