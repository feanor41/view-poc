CREATE SCHEMA IF NOT EXISTS ingest_accounts;
CREATE SCHEMA IF NOT EXISTS ingest_financials;
CREATE SCHEMA IF NOT EXISTS reporting;
CREATE SCHEMA IF NOT EXISTS sync;
CREATE TABLE IF NOT EXISTS ingest_accounts.accounts (
 id uuid PRIMARY KEY, name varchar(200) NOT NULL, status varchar(40) NOT NULL, created_at timestamptz NOT NULL);
CREATE TABLE IF NOT EXISTS ingest_accounts.account_history (
 id uuid PRIMARY KEY, account_id uuid NOT NULL, description varchar(1000) NOT NULL, occurred_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS account_history_account_id_idx ON ingest_accounts.account_history(account_id);
CREATE INDEX IF NOT EXISTS account_history_occurred_at_idx ON ingest_accounts.account_history(occurred_at);
CREATE TABLE IF NOT EXISTS ingest_financials.operations (
 id uuid PRIMARY KEY, account_id uuid NOT NULL, description varchar(500) NOT NULL, amount numeric(18,2) NOT NULL);
CREATE TABLE IF NOT EXISTS ingest_financials.payment_means (id uuid PRIMARY KEY, name varchar(100) NOT NULL);
CREATE TABLE IF NOT EXISTS ingest_financials.transactions (
 id uuid PRIMARY KEY, operation_id uuid NOT NULL, payment_means_id uuid NOT NULL,
 amount numeric(18,2) NOT NULL, occurred_at timestamptz NOT NULL);
CREATE TABLE IF NOT EXISTS ingest_financials.collection_orders (
 id uuid PRIMARY KEY, operation_id uuid NOT NULL, due_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS operations_account_id_idx ON ingest_financials.operations(account_id);
CREATE INDEX IF NOT EXISTS transactions_operation_id_idx ON ingest_financials.transactions(operation_id);
CREATE INDEX IF NOT EXISTS transactions_occurred_at_idx ON ingest_financials.transactions(occurred_at);
CREATE INDEX IF NOT EXISTS collection_orders_operation_id_idx ON ingest_financials.collection_orders(operation_id);
CREATE TABLE IF NOT EXISTS reporting.account_operations (
 operation_id uuid PRIMARY KEY, account_id uuid NOT NULL, account_name varchar(200),
 account_status varchar(40), account_missing boolean NOT NULL, description varchar(500) NOT NULL, amount numeric(18,2) NOT NULL,
 transaction_total numeric NOT NULL, transaction_count bigint NOT NULL,
 last_source_commit_at timestamptz NOT NULL, materialized_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS account_operations_account_id_idx ON reporting.account_operations(account_id);
CREATE TABLE IF NOT EXISTS sync.checkpoints (
 source_name text PRIMARY KEY, database_name text NOT NULL, system_id text NOT NULL,
 slot_name text NOT NULL, lsn pg_lsn NOT NULL, last_commit_at timestamptz,
 applied_at timestamptz NOT NULL, rows_applied bigint NOT NULL DEFAULT 0,
 batches_applied bigint NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS sync.settings (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton), retention_years integer NOT NULL CHECK(retention_years>=5));
CREATE TABLE IF NOT EXISTS sync.events (
 batch_id uuid NOT NULL, sequence integer NOT NULL, source_name text NOT NULL,
 table_name text NOT NULL, action char(1) NOT NULL CHECK(action IN ('I','U','D')),
 row_id uuid NOT NULL, payload jsonb NOT NULL, PRIMARY KEY(batch_id,sequence));
CREATE INDEX IF NOT EXISTS events_row_idx ON sync.events(batch_id,table_name,row_id,sequence DESC);
CREATE TABLE IF NOT EXISTS sync.affected_operations (
 batch_id uuid NOT NULL, operation_id uuid NOT NULL, PRIMARY KEY(batch_id,operation_id));

CREATE OR REPLACE FUNCTION sync.rebuild_projection(commit_at timestamptz) RETURNS void LANGUAGE sql AS $$
 DELETE FROM reporting.account_operations;
 INSERT INTO reporting.account_operations
 SELECT o.id,o.account_id,a.name,a.status,a.id IS NULL,o.description,o.amount,
  coalesce(t.total,0),coalesce(t.count,0),commit_at,clock_timestamp()
 FROM ingest_financials.operations o LEFT JOIN ingest_accounts.accounts a ON a.id=o.account_id
 LEFT JOIN LATERAL (SELECT sum(amount) total,count(*) count FROM ingest_financials.transactions WHERE operation_id=o.id) t ON true;
$$;

-- This function executes inside the same target transaction as COPY and its checkpoint.
CREATE OR REPLACE FUNCTION sync.apply_batch(batch uuid, source text, commit_at timestamptz, max_projection_rows integer, max_projection_bytes bigint)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE item record; target_schema text; columns_sql text; updates_sql text;
BEGIN
 IF source NOT IN ('accounts','financials') THEN RAISE EXCEPTION 'Unknown source'; END IF;
 target_schema := 'ingest_'||source;
 -- Capture old and new dependencies before replacing mirror rows.
 INSERT INTO sync.affected_operations
 SELECT DISTINCT batch,o.id FROM ingest_financials.operations o JOIN sync.events e
 ON e.batch_id=batch AND e.source_name='accounts' AND e.table_name='accounts' AND o.account_id=e.row_id
 ON CONFLICT DO NOTHING;
 INSERT INTO sync.affected_operations
 SELECT DISTINCT batch,e.row_id FROM sync.events e WHERE e.batch_id=batch AND e.table_name='operations'
 ON CONFLICT DO NOTHING;
 INSERT INTO sync.affected_operations
 SELECT DISTINCT batch,t.operation_id FROM ingest_financials.transactions t JOIN sync.events e
 ON e.batch_id=batch AND e.table_name='transactions' AND t.id=e.row_id
 ON CONFLICT DO NOTHING;
 INSERT INTO sync.affected_operations
 SELECT DISTINCT batch,(payload->>'operation_id')::uuid FROM sync.events
 WHERE batch_id=batch AND table_name='transactions' AND payload->>'operation_id' IS NOT NULL
 ON CONFLICT DO NOTHING;
 IF (SELECT count(*) FROM sync.affected_operations WHERE batch_id=batch)>max_projection_rows
 THEN RAISE EXCEPTION 'Projection fanout exceeds configured bound; transaction rolled back without checkpoint advancement'; END IF;
 -- Conservative output estimate: worst-case UTF-8 account name/status (960 bytes),
 -- fixed identifiers/amounts/aggregates/timestamps/envelope (256 bytes), and the
 -- larger current/incoming operation description. It is not a database memory
 -- or execution-time limit, and can reject work whose exact output is smaller.
 IF (SELECT coalesce(sum(1216::bigint + greatest(octet_length(coalesce(o.description,'')),
     coalesce(incoming.description_bytes,0))),0)
   FROM sync.affected_operations d LEFT JOIN ingest_financials.operations o ON o.id=d.operation_id
   LEFT JOIN LATERAL (SELECT max(octet_length(payload->>'description')) description_bytes FROM sync.events e WHERE e.batch_id=batch
    AND e.table_name='operations' AND e.row_id=d.operation_id) incoming ON true
   WHERE d.batch_id=batch)>max_projection_bytes
 THEN RAISE EXCEPTION 'Estimated projection bytes exceed configured bound; transaction rolled back without checkpoint advancement'; END IF;
 FOR item IN SELECT DISTINCT table_name FROM sync.events WHERE batch_id=batch LOOP
  IF (source='accounts' AND item.table_name NOT IN ('accounts','account_history')) OR
   (source='financials' AND item.table_name NOT IN ('operations','transactions','payment_means','collection_orders'))
   THEN RAISE EXCEPTION 'Unsupported relation'; END IF;
  SELECT string_agg(quote_ident(column_name),',' ORDER BY ordinal_position),
   string_agg(format('%I=EXCLUDED.%I',column_name,column_name),',' ORDER BY ordinal_position)
   FILTER(WHERE column_name<>'id') INTO columns_sql,updates_sql
   FROM information_schema.columns WHERE table_schema=target_schema AND table_name=item.table_name;
  EXECUTE format('DELETE FROM %I.%I m USING
   (SELECT DISTINCT ON(row_id) row_id,action FROM sync.events WHERE batch_id=$1 AND table_name=$2 ORDER BY row_id,sequence DESC) e
   WHERE m.id=e.row_id AND e.action=''D''',target_schema,item.table_name) USING batch,item.table_name;
  -- Merge each column from its last event to preserve unchanged TOAST values.
  EXECUTE format('INSERT INTO %I.%I (%s)
   SELECT (jsonb_populate_record(NULL::%I.%I,coalesce(to_jsonb(m),''{}''::jsonb)||p.payload)).*
   FROM (SELECT DISTINCT ON(row_id) row_id,action FROM sync.events WHERE batch_id=$1 AND table_name=$2 ORDER BY row_id,sequence DESC) e
   LEFT JOIN %I.%I m ON m.id=e.row_id
   JOIN LATERAL (SELECT jsonb_object_agg(key,value) payload FROM
    (SELECT DISTINCT ON(j.key) j.key,j.value FROM sync.events s CROSS JOIN LATERAL jsonb_each(s.payload) j
     WHERE s.batch_id=$1 AND s.table_name=$2 AND s.row_id=e.row_id ORDER BY j.key,s.sequence DESC) fields) p ON true
   WHERE e.action<>''D'' ON CONFLICT(id) DO UPDATE SET %s',
   target_schema,item.table_name,columns_sql,target_schema,item.table_name,target_schema,item.table_name,updates_sql)
   USING batch,item.table_name;
 END LOOP;
 DELETE FROM reporting.account_operations r USING sync.affected_operations d WHERE d.batch_id=batch AND r.operation_id=d.operation_id;
 INSERT INTO reporting.account_operations
 SELECT o.id,o.account_id,a.name,a.status,a.id IS NULL,o.description,o.amount,
  coalesce(t.total,0),coalesce(t.count,0),commit_at,clock_timestamp()
 FROM sync.affected_operations d JOIN ingest_financials.operations o ON o.id=d.operation_id
 LEFT JOIN ingest_accounts.accounts a ON a.id=o.account_id
 LEFT JOIN LATERAL(SELECT sum(amount) total,count(*) count FROM ingest_financials.transactions WHERE operation_id=o.id) t ON true
 WHERE d.batch_id=batch;
 DELETE FROM sync.affected_operations WHERE batch_id=batch;
 DELETE FROM sync.events WHERE batch_id=batch;
END $$;

-- Production sizing evaluation: these ordinary PoC mirror tables do not demonstrate
-- nine billion row storage. Consider monthly occurred_at range partitions plus a
-- UUID-to-partition locator for deletes/reparenting; partitioned uniqueness includes
-- the partition key. Index operation_id within each partition for recomputation.
-- Retention is configurable >=5 years. No purge, partition drop or expiry job exists.
