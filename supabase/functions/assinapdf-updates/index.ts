import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient } from "npm:@supabase/supabase-js@2.98.0";

const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const supabase = createClient(supabaseUrl, serviceRoleKey, {
  auth: { persistSession: false, autoRefreshToken: false },
});

const allowedOrigins = new Set([
  "https://crm.futuresolucoes.com.br",
  "https://spc-vision-crm.vercel.app",
]);

function corsHeaders(origin: string | null) {
  const allowed = origin && (allowedOrigins.has(origin) ||
    (origin.startsWith("https://spc-vision-crm-") && origin.endsWith(".vercel.app")));
  return {
    "Access-Control-Allow-Origin": allowed ? origin! : "*",
    "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Access-Control-Max-Age": "86400",
    "Vary": "Origin",
  };
}

function reply(data: unknown, status = 200, origin: string | null = null) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { ...corsHeaders(origin), "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" },
  });
}

function platformAllowed(value: string) {
  return ["windows-x64", "macos-x64", "macos-arm64", "macos-universal"].includes(value);
}

Deno.serve(async (request: Request) => {
  const origin = request.headers.get("Origin");
  if (request.method === "OPTIONS") return new Response("ok", { headers: corsHeaders(origin) });
  if (request.method !== "POST") return reply({ error: "Método não permitido." }, 405, origin);

  try {
    const body = await request.json().catch(() => ({}));
    const platform = String(body?.platform || "");
    const channel = String(body?.channel || "stable");
    const currentVersion = String(body?.current_version || "").trim();

    if (!platformAllowed(platform)) return reply({ error: "Plataforma não suportada." }, 400, origin);
    if (!["stable", "beta"].includes(channel)) return reply({ error: "Canal inválido." }, 400, origin);
    if (!/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(currentVersion))
      return reply({ error: "Versão atual inválida." }, 400, origin);

    const { data, error } = await supabase
      .from("assinapdf_releases")
      .select("id,platform,channel,version,build_number,minimum_supported_version,download_url,sha256,signature,release_notes,is_mandatory,published_at")
      .eq("platform", platform)
      .eq("channel", channel)
      .eq("is_published", true)
      .not("published_at", "is", null)
      .order("published_at", { ascending: false })
      .limit(20);

    if (error) {
      console.error("[assinapdf-updates] query failed", error.message);
      return reply({ error: "Não foi possível consultar as atualizações." }, 500, origin);
    }

    const parseVersion = (value: string) => {
      const clean = value.split(/[+-]/, 1)[0];
      const parts = clean.split(".").map(Number);
      return parts.length === 3 && parts.every(Number.isSafeInteger) ? parts : null;
    };
    const compare = (a: number[], b: number[]) => {
      for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] > b[i] ? 1 : -1;
      return 0;
    };

    const current = parseVersion(currentVersion);
    const valid = (data || []).filter((item: any) => parseVersion(String(item.version)));
    valid.sort((a: any, b: any) =>
      compare(parseVersion(String(b.version))!, parseVersion(String(a.version))!) ||
      Number(b.build_number || 0) - Number(a.build_number || 0));

    const latest = valid[0] || null;
    const latestParts = latest ? parseVersion(String(latest.version)) : null;
    const updateAvailable = !!latest && !!current && compare(latestParts!, current) > 0;

    return reply({
      update_available: updateAvailable,
      current_version: currentVersion,
      platform,
      channel,
      release: updateAvailable ? latest : null,
      checked_at: new Date().toISOString(),
    }, 200, origin);
  } catch (error) {
    console.error("[assinapdf-updates]", error);
    return reply({ error: "Falha ao verificar atualizações." }, 500, origin);
  }
});
