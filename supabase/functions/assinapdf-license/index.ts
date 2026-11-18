import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "npm:@supabase/supabase-js@2.98.0";

const url = Deno.env.get("SUPABASE_URL")!;
const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const supabase = createClient(url, serviceKey, {
  auth: { persistSession: false, autoRefreshToken: false },
});

function reply(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8" },
  });
}

async function sha256(value: string) {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, "0")).join("");
}

Deno.serve(async (request: Request) => {
  if (request.method !== "POST") return reply({ error: "Método não permitido." }, 405);
  try {
    const token = (request.headers.get("Authorization") || "").replace(/^Bearer\s+/i, "").trim();
    if (!token) return reply({ error: "Entre na sua conta AssinaPDF para ativar a licença." }, 401);

    const { data: authData, error: authError } = await supabase.auth.getUser(token);
    const user = authData?.user;
    if (authError || !user?.id || !user.email) {
      return reply({ error: "Sessão inválida ou expirada. Entre novamente." }, 401);
    }

    const body = await request.json().catch(() => ({}));
    const action = String(body?.action || "");
    if (action === "redeem_key") {
      const key = String(body?.key || "").trim().toUpperCase().replace(/\s+/g, "");
      if (!/^FUT-[A-F0-9]{8}-[A-F0-9]{8}-[A-F0-9]{8}-[A-F0-9]{8}$/.test(key)) {
        return reply({ error: "Confira o formato da chave de ativação." }, 400);
      }
      const keyHash = await sha256(key);
      const { data, error } = await supabase.rpc("redeem_assinapdf_activation_key", {
        p_key_hash: keyHash,
        p_user_id: user.id,
        p_email: String(user.email).toLowerCase(),
      });
      if (error) {
        const message = String(error.message || "");
        const known = [
          "Chave não encontrada.", "Esta chave já foi utilizada.", "Esta chave foi cancelada.",
          "O pagamento desta chave ainda não foi confirmado.", "Esta chave não está disponível.",
          "Esta chave expirou.", "Esta chave foi emitida para outro e-mail.",
          "O plano desta chave não está disponível.", "Dados de ativação inválidos.",
          "Esta conta possui checkout ou assinatura Asaas pendente/ativa. Solicite ao suporte o cancelamento antes de ativar uma chave manual.",
        ];
        const userMessage = known.find(item => message.includes(item)) || "Não foi possível ativar esta chave.";
        return reply({ error: userMessage }, 400);
      }
      return reply({ activation: data });
    }


    if (action === "register_device") {
      const fingerprint = String(body?.device_fingerprint || "").trim();
      if (fingerprint.length < 32 || fingerprint.length > 256) {
        return reply({ error: "Identificação do dispositivo inválida." }, 400);
      }
      const fingerprintHash = await sha256(fingerprint);
      const { data, error } = await supabase.rpc("register_assinapdf_device", {
        p_user_id: user.id,
        p_fingerprint_hash: fingerprintHash,
        p_device_label: String(body?.device_label || "").slice(0, 120),
        p_platform: String(body?.platform || "unknown").slice(0, 40),
        p_app_version: String(body?.app_version || "").slice(0, 40),
      });
      if (error) {
        const message = String(error.message || "");
        if (message.includes("Limite de dispositivos atingido.")) {
          return reply({ error: "Limite de dispositivos atingido. Entre em contato com a Future Soluções para liberar outro equipamento." }, 409);
        }
        if (message.includes("Não existe uma licença Premium ativa")) {
          return reply({ error: "Não existe uma licença Premium ativa para esta conta." }, 403);
        }
        return reply({ error: "Não foi possível registrar este dispositivo." }, 400);
      }
      return reply({ device: data });
    }

    if (action === "license_status") {
      await supabase.from("assinapdf_licenses")
        .update({ status: "expired", updated_at: new Date().toISOString() })
        .eq("user_id", user.id).eq("status", "active")
        .lte("valid_until", new Date().toISOString());
      const { data, error } = await supabase
        .from("assinapdf_licenses")
        .select("id,status,valid_from,valid_until,max_devices,assinapdf_plans(code,name,billing_interval)")
        .eq("user_id", user.id)
        .order("created_at", { ascending: false })
        .limit(1)
        .maybeSingle();
      if (error) throw error;
      return reply({ license: data || null });
    }

    return reply({ error: "Ação não reconhecida." }, 400);
  } catch (error) {
    console.error("[assinapdf-license]", error);
    return reply({ error: "Não foi possível concluir a operação de licença." }, 500);
  }
});