#!/usr/bin/env bash
# Teste ponta a ponta: com o docker compose no ar, faz uma transferência e confere que ela atravessou o sistema
# inteiro (ledger → outbox → RabbitMQ → processor) e apareceu nos traces, nas métricas e no consolidado.
set -euo pipefail

LEDGER=http://localhost:8080
PROCESSOR=http://localhost:8081
JAEGER=http://localhost:16686
PROMETHEUS=http://localhost:9090
GRAFANA=http://localhost:3000

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'FALHOU: %s\n' "$*" >&2; exit 1; }

# Repete o comando até ele dar certo ou o tempo acabar.
retry() {
  local timeout=$1 description=$2; shift 2
  local deadline=$((SECONDS + timeout))
  until "$@" >/dev/null 2>&1; do
    ((SECONDS < deadline)) || fail "$description (esperou ${timeout}s)"
    sleep 2
  done
}

step "Esperando os serviços"
retry 120 "ledger no ar" curl -fsS "$LEDGER/health"
retry 60 "processor no ar" curl -fsS "$PROCESSOR/health"
retry 60 "grafana no ar" curl -fsS "$GRAFANA/api/health"

step "Abrindo contas e movimentando dinheiro"
open_account() {
  curl -fsS -X POST "$LEDGER/accounts" -H 'Content-Type: application/json' \
    -d "{\"ownerName\":\"$1\",\"currency\":\"BRL\"}" | jq -r .id
}
source_account=$(open_account "Origem E2E")
destination_account=$(open_account "Destino E2E")

curl -fsS -X POST "$LEDGER/accounts/$source_account/deposits" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: e2e-deposit-$source_account" -d '{"amount":10000}' >/dev/null

# Trace ID conhecido, para buscar esse trace no Jaeger depois.
trace_id=$(openssl rand -hex 16)
curl -fsS -X POST "$LEDGER/transfers" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: e2e-transfer-$source_account" \
  -H "traceparent: 00-$trace_id-$(openssl rand -hex 8)-01" \
  -d "{\"sourceAccountId\":\"$source_account\",\"destinationAccountId\":\"$destination_account\",\"amount\":2500}" >/dev/null
echo "trace: $JAEGER/trace/$trace_id"

step "Consolidado do dia no processor"
today=$(date -u +%F)
has_transfer() {
  curl -fsS "$PROCESSOR/settlements?date=$today" \
    | jq -e '.settlements[] | select(.kind == "Transfer" and .totalAmount >= 2500)'
}
retry 60 "transferência no consolidado" has_transfer

step "Trace atravessando os dois serviços no Jaeger"
trace_has_both_services() {
  curl -fsS "$JAEGER/api/traces/$trace_id" \
    | jq -e '[.data[0].processes[].serviceName] | (index("payflow-ledger") != null) and (index("payflow-processor") != null)'
}
retry 60 "trace com spans do ledger e do processor" trace_has_both_services
curl -fsS "$JAEGER/api/traces/$trace_id" \
  | jq -r '.data[0] as $t | $t.spans | sort_by(.startTime)[] | "  \($t.processes[.processID].serviceName): \(.operationName)"'

step "Métricas no Prometheus"
has_metric() {
  curl -fsS --get "$PROMETHEUS/api/v1/query" --data-urlencode "query=$1" | jq -e '.data.result | length > 0'
}
for metric in \
  'payflow_ledger_transactions_total' \
  'payflow_outbox_publish_delay_seconds_count' \
  'payflow_processor_messages_total{outcome="processed"}' \
  'payflow_processor_end_to_end_lag_seconds_count' \
  'http_server_request_duration_seconds_count{job="payflow-ledger"}'; do
  retry 60 "métrica $metric" has_metric "$metric"
  echo "  ok: $metric"
done

step "Dashboard provisionado no Grafana"
curl -fsS "$GRAFANA/api/dashboards/uid/payflow-overview" | jq -e '.dashboard.title == "PayFlow"' >/dev/null \
  || fail "dashboard PayFlow não encontrado"
echo "  ok: dashboard PayFlow"

printf '\nTudo certo.\n'
