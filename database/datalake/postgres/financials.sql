-- No cross-database source foreign key: eventual source arrivals are reconciled in Datalake.
CREATE TABLE IF NOT EXISTS public.operations (
 id uuid PRIMARY KEY, account_id uuid NOT NULL, description varchar(500) NOT NULL,
 amount numeric(18,2) NOT NULL);
CREATE TABLE IF NOT EXISTS public.payment_means (id uuid PRIMARY KEY, name varchar(100) NOT NULL);
CREATE TABLE IF NOT EXISTS public.transactions (
 id uuid PRIMARY KEY, operation_id uuid NOT NULL REFERENCES public.operations(id),
 payment_means_id uuid NOT NULL REFERENCES public.payment_means(id), amount numeric(18,2) NOT NULL,
 occurred_at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE TABLE IF NOT EXISTS public.collection_orders (
 id uuid PRIMARY KEY, operation_id uuid NOT NULL REFERENCES public.operations(id), due_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS operations_account_id_idx ON public.operations(account_id);
CREATE INDEX IF NOT EXISTS transactions_operation_id_idx ON public.transactions(operation_id);
CREATE INDEX IF NOT EXISTS transactions_occurred_at_idx ON public.transactions(occurred_at);
CREATE INDEX IF NOT EXISTS collection_orders_operation_id_idx ON public.collection_orders(operation_id);
DO $$ BEGIN
 IF NOT EXISTS (SELECT FROM pg_publication WHERE pubname='vwp_datalake') THEN
  CREATE PUBLICATION vwp_datalake FOR TABLE public.operations, public.payment_means,
   public.transactions, public.collection_orders;
 END IF;
END $$;
