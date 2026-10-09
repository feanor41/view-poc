#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} == --help || ${1:-} == -h ]]; then
  cat <<'HELP'
Usage: scripts/datalake-test-runtime.sh up [--runtime-dir /tmp/vwp11-runtime]
Create or reuse isolated SQL Server 2022 CU23 and PostgreSQL 18.3 test containers.
Requires Docker and Python 3. Uses loopback ports 15043 and 15433.
Credentials remain in a private runtime directory outside Git; no cluster actions.
Existing containers require the VWP-11 ownership label and existing worker.env.
No automatic cleanup or credential rotation is performed.
HELP
  exit 0
fi
if [[ ${1:-} != up ]]; then
  echo 'Expected up; use --help.' >&2
  exit 2
fi
shift
runtime_dir=/tmp/vwp11-runtime
if [[ $# -gt 0 ]]; then
  if [[ $# != 2 || $1 != --runtime-dir ]]; then
    echo 'Expected --runtime-dir PATH; use --help.' >&2
    exit 2
  fi
  runtime_dir=$2
fi
python3 - "$runtime_dir" <<'PY'
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time

runtime = Path(sys.argv[1]).resolve()
if runtime == Path('/') or any((parent / '.git').exists() for parent in (runtime, *runtime.parents)):
    raise SystemExit('Runtime directory must be outside every Git checkout.')
if runtime.exists() and (not runtime.is_dir() or runtime.stat().st_mode & 0o077):
    raise SystemExit('Existing runtime directory must be private (mode 0700).')
# Validate ownership before making files or starting a container.
names = ('vwp11-sqlserver-test', 'vwp11-postgres-test')
existing = {}
for name in names:
    query = subprocess.run(['docker', 'inspect', '--format', '{{json .Config.Labels}}', name],
                           capture_output=True, text=True)
    if query.returncode == 0:
        labels = json.loads(query.stdout)
        if (labels or {}).get('pitcrew.issue') != 'VWP-11':
            raise SystemExit('Refusing an existing container without VWP-11 ownership.')
        existing[name] = True
    else:
        # Distinguish an absent name from an unavailable daemon.
        check = subprocess.run(['docker', 'info', '--format', '{{.ServerVersion}}'], capture_output=True)
        if check.returncode:
            raise SystemExit('Docker daemon access failed; no runtime changes made.')
        existing[name] = False
path = runtime / 'worker.env'
if any(existing.values()) and not path.exists():
    raise SystemExit('Owned containers exist without worker.env; refusing to invent or rotate credentials.')
runtime.mkdir(parents=True, exist_ok=True, mode=0o700)
if path.exists():
    if path.stat().st_mode & 0o077:
        raise SystemExit('Existing worker.env must be private (mode 0600).')
    values = {}
    for line in path.read_text().splitlines():
        if not line.strip() or line.startswith('#'):
            continue
        key, separator, value = line.partition('=')
        if not separator:
            raise SystemExit('Invalid existing worker.env; no credential rotation performed.')
        values[key] = value
else:
    sql_password = 'Aa1!' + secrets.token_hex(24)
    pg_password = secrets.token_hex(24)
    values = {
        'VWP_DATALAKE_DISPOSABLE': '1',
        'VWP_SQLSERVER_CONTAINER': names[0],
        'VWP_POSTGRES_CONTAINER': names[1],
        'VWP_SQLSERVER_CONNECTION': f'Server=127.0.0.1,15043;User ID=sa;Password={sql_password};TrustServerCertificate=True;Encrypt=True',
        'VWP_POSTGRES_CONNECTION': f'Host=127.0.0.1;Port=15433;Username=postgres;Password={pg_password};Database=postgres',
        'VWP_POSTGRES_SLOT_PREFIX': 'vwp11_native',
    }
    for engine in ('SQLSERVER', 'POSTGRES'):
        for role in ('ACCOUNTS', 'FINANCIALS', 'DATALAKE'):
            values[f'VWP_{engine}_{role}_DATABASE'] = 'vwp11_' + role.lower()
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as file:
        file.write(''.join(f'{key}={value}\n' for key, value in values.items()))
if values.get('VWP_DATALAKE_DISPOSABLE') != '1' or any(values.get(key) != name for key, name in
    zip(('VWP_SQLSERVER_CONTAINER', 'VWP_POSTGRES_CONTAINER'), names)):
    raise SystemExit('Existing worker.env does not describe this isolated runtime.')
for engine in ('SQLSERVER', 'POSTGRES'):
    for role in ('ACCOUNTS', 'FINANCIALS', 'DATALAKE'):
        if values.get(f'VWP_{engine}_{role}_DATABASE') != 'vwp11_' + role.lower():
            raise SystemExit('Existing environment has unexpected database names; no mutation performed.')

def fields(connection):
    result = {}
    for field in connection.split(';'):
        if not field:
            continue
        key, separator, value = field.partition('=')
        if not separator or key.strip().lower() in result:
            raise SystemExit('Existing connection format is unsupported; no runtime mutation performed.')
        result[key.strip().lower()] = value.strip()
    return result

sql_fields = fields(values.get('VWP_SQLSERVER_CONNECTION', ''))
pg_fields = fields(values.get('VWP_POSTGRES_CONNECTION', ''))
if sql_fields.get('server') != '127.0.0.1,15043' or pg_fields.get('host') != '127.0.0.1' or pg_fields.get('port') != '15433':
    raise SystemExit('Connection endpoints must match the isolated loopback runtime; no container mutation performed.')

def password(connection):
    # This helper creates hexadecimal credentials with no separators or quotes.
    for field in connection.split(';'):
        key, _, value = field.partition('=')
        if key.strip().lower() == 'password' and value:
            return value
    raise SystemExit('Existing connection has no supported password field; refusing credential rotation.')

configs = [
    (names[0], 'mcr.microsoft.com/mssql/server:2022-CU23-ubuntu-22.04', '3g', '127.0.0.1:15043:1433',
     {'ACCEPT_EULA': 'Y', 'MSSQL_PID': 'Developer', 'MSSQL_SA_PASSWORD': password(values['VWP_SQLSERVER_CONNECTION'])}, []),
    (names[1], 'postgres:18.3', '1g', '127.0.0.1:15433:5432',
     {'POSTGRES_PASSWORD': password(values['VWP_POSTGRES_CONNECTION'])},
     ['-c', 'wal_level=logical', '-c', 'max_replication_slots=10', '-c', 'max_wal_senders=10']),
]
for name, image, memory, port, environment, arguments in configs:
    if existing[name]:
        # Require an already running runtime; restarting is an explicit operator action.
        query = subprocess.run(['docker', 'inspect', '--format', '{{.State.Running}}|{{.Config.Image}}|{{json .HostConfig.PortBindings}}', name], capture_output=True, text=True)
        expected_port = '15043' if 'sqlserver' in name else '15433'
        if query.returncode or not query.stdout.startswith('true|' + image + '|') or '127.0.0.1' not in query.stdout or expected_port not in query.stdout:
            raise SystemExit('Existing runtime image/running state/loopback mapping does not match; no restart performed.')
        continue
    command = ['docker', 'run', '-d', '--name', name, '--label', 'pitcrew.issue=VWP-11', '--memory', memory,
               '--publish', port]
    for key in environment:
        command.extend(['--env', key])  # Docker reads values from the subprocess environment.
    command.extend([image, *arguments])
    run = subprocess.run(command, env=dict(os.environ) | environment, capture_output=True)
    if run.returncode:
        raise SystemExit('Container creation failed; private environment and any created runtime are retained.')

for name in names:
    deadline = time.monotonic() + 120
    if 'sqlserver' in name:
        probe = ['docker', 'exec', name, 'sh', '-c', 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q "SELECT 1"']
    else:
        probe = ['docker', 'exec', '-u', 'postgres', name, 'psql', '-X', '-At', '-c', 'SHOW wal_level;']
    while True:
        check = subprocess.run(probe, capture_output=True, timeout=15)
        if check.returncode == 0 and ('postgres' not in name or check.stdout.strip() == b'logical'):
            break
        if time.monotonic() > deadline:
            raise SystemExit('Isolated runtime readiness timed out; containers are retained for investigation.')
        time.sleep(1)
print(f'Isolated runtime ready. Private environment: {path}')
print('No credentials were printed. Containers and synthetic data are retained; no cluster actions performed.')
PY
