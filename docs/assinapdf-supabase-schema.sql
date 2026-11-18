-- AssinaPDF - estrutura do Supabase dedicado
-- Projeto: zcquhikpsqohrclflnjo
-- Este arquivo documenta o schema aplicado no projeto remoto. Não contém chaves secretas.
-- O acesso às tabelas é feito exclusivamente pelas Edge Functions com service_role.
-- Não conceder acesso direto a anon/authenticated.

create table if not exists public.assinapdf_plans (
  id uuid primary key default gen_random_uuid(),
  code text not null unique,
  name text not null,
  description text,
  price_cents bigint,
  billing_interval text check (billing_interval in ('monthly','yearly','lifetime') or billing_interval is null),
  device_limit integer not null default 1 check (device_limit > 0),
  is_active boolean not null default true,
  sort_order integer not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint assinapdf_plans_price_nonnegative check (price_cents is null or price_cents >= 0)
);

create table if not exists public.assinapdf_plan_features (
  id uuid primary key default gen_random_uuid(),
  plan_id uuid not null references public.assinapdf_plans(id) on delete cascade,
  feature_key text not null,
  is_enabled boolean not null default true,
  numeric_limit bigint,
  config jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (plan_id, feature_key),
  constraint assinapdf_feature_limit_nonnegative check (numeric_limit is null or numeric_limit >= 0)
);

create table if not exists public.assinapdf_customers (
  user_id uuid primary key references auth.users(id) on delete cascade,
  email text not null,
  full_name text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.assinapdf_subscriptions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  plan_id uuid not null references public.assinapdf_plans(id),
  status text not null default 'pending' check (status in ('pending','trialing','active','past_due','canceled','expired')),
  provider text,
  external_customer_id text,
  external_subscription_id text,
  current_period_start timestamptz,
  current_period_end timestamptz,
  cancel_at_period_end boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.assinapdf_licenses (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  subscription_id uuid references public.assinapdf_subscriptions(id) on delete set null,
  plan_id uuid not null references public.assinapdf_plans(id),
  license_key_hash text unique,
  license_code_prefix text,
  status text not null default 'pending' check (status in ('pending','active','suspended','revoked','expired')),
  max_devices integer not null default 1 check (max_devices > 0),
  valid_from timestamptz not null default now(),
  valid_until timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.assinapdf_devices (
  id uuid primary key default gen_random_uuid(),
  license_id uuid not null references public.assinapdf_licenses(id) on delete cascade,
  device_fingerprint_hash text not null,
  device_label text,
  platform text not null check (platform in ('windows-x64','macos-x64','macos-arm64','macos-universal')),
  app_version text,
  activated_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  revoked_at timestamptz,
  unique (license_id, device_fingerprint_hash)
);

create table if not exists public.assinapdf_releases (
  id uuid primary key default gen_random_uuid(),
  platform text not null check (platform in ('windows-x64','macos-x64','macos-arm64','macos-universal')),
  channel text not null default 'stable' check (channel in ('stable','beta')),
  version text not null,
  build_number integer not null default 1,
  minimum_supported_version text,
  download_url text not null,
  sha256 text not null,
  signature text,
  release_notes text,
  is_mandatory boolean not null default false,
  is_published boolean not null default false,
  published_at timestamptz,
  created_at timestamptz not null default now(),
  unique (platform, channel, version)
);

create table if not exists public.assinapdf_update_events (
  id uuid primary key default gen_random_uuid(),
  license_id uuid references public.assinapdf_licenses(id) on delete set null,
  device_id uuid references public.assinapdf_devices(id) on delete set null,
  event_type text not null,
  current_version text,
  target_version text,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

create table if not exists public.assinapdf_admin_users (
  crm_user_id uuid primary key,
  email text not null unique,
  admin_role text not null default 'gestor' check (admin_role in ('owner','gestor')),
  is_active boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.assinapdf_admin_audit (
  id uuid primary key default gen_random_uuid(),
  crm_user_id uuid,
  actor_email text not null,
  action text not null,
  target_type text,
  target_id text,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

create index if not exists assinapdf_subscriptions_user_status_idx on public.assinapdf_subscriptions(user_id, status);
create index if not exists assinapdf_licenses_user_status_idx on public.assinapdf_licenses(user_id, status);
create index if not exists assinapdf_devices_license_active_idx on public.assinapdf_devices(license_id) where revoked_at is null;
create index if not exists assinapdf_releases_lookup_idx on public.assinapdf_releases(platform, channel, is_published, published_at desc);
create index if not exists assinapdf_update_events_created_idx on public.assinapdf_update_events(created_at desc);
create index if not exists assinapdf_licenses_plan_id_idx on public.assinapdf_licenses(plan_id);
create index if not exists assinapdf_licenses_subscription_id_idx on public.assinapdf_licenses(subscription_id);
create index if not exists assinapdf_subscriptions_plan_id_idx on public.assinapdf_subscriptions(plan_id);
create index if not exists assinapdf_update_events_device_id_idx on public.assinapdf_update_events(device_id);
create index if not exists assinapdf_update_events_license_id_idx on public.assinapdf_update_events(license_id);

alter table public.assinapdf_plans enable row level security;
alter table public.assinapdf_plan_features enable row level security;
alter table public.assinapdf_customers enable row level security;
alter table public.assinapdf_subscriptions enable row level security;
alter table public.assinapdf_licenses enable row level security;
alter table public.assinapdf_devices enable row level security;
alter table public.assinapdf_releases enable row level security;
alter table public.assinapdf_update_events enable row level security;
alter table public.assinapdf_admin_users enable row level security;
alter table public.assinapdf_admin_audit enable row level security;

revoke all on table
  public.assinapdf_plans, public.assinapdf_plan_features, public.assinapdf_customers,
  public.assinapdf_subscriptions, public.assinapdf_licenses, public.assinapdf_devices,
  public.assinapdf_releases, public.assinapdf_update_events, public.assinapdf_admin_users,
  public.assinapdf_admin_audit
from anon, authenticated, public;

grant all privileges on table
  public.assinapdf_plans, public.assinapdf_plan_features, public.assinapdf_customers,
  public.assinapdf_subscriptions, public.assinapdf_licenses, public.assinapdf_devices,
  public.assinapdf_releases, public.assinapdf_update_events, public.assinapdf_admin_users,
  public.assinapdf_admin_audit
to service_role;

alter default privileges for role postgres in schema public
  revoke all on tables from public, anon, authenticated;
alter default privileges for role postgres in schema public
  grant all on tables to service_role;
alter default privileges for role postgres in schema public
  revoke execute on functions from public, anon, authenticated;
alter default privileges for role postgres in schema public
  grant execute on functions to service_role;

insert into public.assinapdf_plans (code, name, description, price_cents, billing_interval, device_limit, sort_order)
values
  ('free', 'Gratuito', 'Plano gratuito do AssinaPDF', 0, null, 1, 10),
  ('premium', 'Premium', 'Plano pago do AssinaPDF; preço e periodicidade definidos no painel', null, null, 1, 20)
on conflict (code) do nothing;

insert into public.assinapdf_plan_features (plan_id, feature_key, is_enabled)
select p.id, f.feature_key, f.is_enabled
from public.assinapdf_plans p
join (values
  ('free','pdf.sign',true),
  ('free','pdf.validate',true),
  ('free','pdf.edit',false),
  ('premium','pdf.sign',true),
  ('premium','pdf.validate',true),
  ('premium','pdf.edit',true)
) as f(plan_code,feature_key,is_enabled) on f.plan_code=p.code
on conflict (plan_id,feature_key) do update
set is_enabled=excluded.is_enabled, updated_at=now();

create schema if not exists private;
revoke all on schema private from public, anon, authenticated;

create or replace function private.assinapdf_sync_customer_profile()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  insert into public.assinapdf_customers (user_id, email, full_name)
  values (
    new.id,
    coalesce(new.email, ''),
    nullif(coalesce(new.raw_user_meta_data ->> 'full_name', new.raw_user_meta_data ->> 'display_name'), '')
  )
  on conflict (user_id) do update
    set email = excluded.email,
        full_name = coalesce(excluded.full_name, public.assinapdf_customers.full_name),
        updated_at = now();
  return new;
end;
$$;

revoke all on function private.assinapdf_sync_customer_profile() from public, anon, authenticated;

drop trigger if exists assinapdf_sync_customer_profile on auth.users;
create trigger assinapdf_sync_customer_profile
after insert or update on auth.users
for each row execute function private.assinapdf_sync_customer_profile();

-- Inserir aqui os administradores autorizados do CRM Vision após conferir os IDs em auth.users.
-- A tabela assinapdf_admin_users é a allowlist validada pela Edge Function.
