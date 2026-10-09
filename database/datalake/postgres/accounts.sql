-- Explicit bootstrap only. Synthetic equivalents of the existing Accounts entities.
CREATE TABLE IF NOT EXISTS public.accounts (
 id uuid PRIMARY KEY, name varchar(200) NOT NULL, status varchar(40) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE TABLE IF NOT EXISTS public.account_history (
 id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES public.accounts(id),
 description varchar(1000) NOT NULL, occurred_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE INDEX IF NOT EXISTS account_history_account_id_idx ON public.account_history(account_id);
CREATE INDEX IF NOT EXISTS account_history_occurred_at_idx ON public.account_history(occurred_at);
DO $$ BEGIN
 IF NOT EXISTS (SELECT FROM pg_publication WHERE pubname='vwp_datalake') THEN
  CREATE PUBLICATION vwp_datalake FOR TABLE public.accounts, public.account_history;
 END IF;
END $$;
