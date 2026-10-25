#!/usr/bin/env bash
# Depois do k6: espera o outbox e o processor zerarem a fila e confere, direto nos bancos, que o sistema continuou
# correto sob carga. Escreve o resultado em Markdown em $RESULTS_DIR/consistency.md.
set -euo pipefail

RESULTS_DIR=${RESULTS_DIR:-loadtest/results}
TIMEOUT=${DRAIN_TIMEOUT:-300}

ledger_sql() { docker compose exec -T postgres psql -U payflow -d ledger -tAc "$1" | tr -d '[:space:]'; }
processor_sql() { docker compose exec -T postgres psql -U payflow -d processor -tAc "$1" | tr -d '[:space:]'; }
queue_depth() {
  docker compose exec -T rabbitmq rabbitmqctl list_queues -q name messages \
    | awk -v q="$1" '$1 == q { print $2 }'
}

ledger_transfers() { ledger_sql "SELECT count(*) || '/' || coalesce(sum(amount), 0) FROM ledger_transactions WHERE kind = 'Transfer'"; }
processor_transfers() { processor_sql "SELECT coalesce(sum(transaction_count), 0) || '/' || coalesce(sum(total_amount), 0) FROM daily_settlements WHERE kind = 'Transfer'"; }
outbox_pending() { ledger_sql "SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL"; }

echo "==> Esperando o outbox e o processor esvaziarem (até ${TIMEOUT}s)"
started=$SECONDS
until [[ "$(outbox_pending)" == "0" && "$(ledger_transfers)" == "$(processor_transfers)" ]]; do
  if ((SECONDS - started > TIMEOUT)); then
    echo "FALHOU: não esvaziou em ${TIMEOUT}s (outbox pendente: $(outbox_pending), ledger: $(ledger_transfers), processor: $(processor_transfers))" >&2
    exit 1
  fi
  sleep 1
done
drain=$((SECONDS - started))

deposits=$(ledger_sql "SELECT coalesce(sum(amount), 0) FROM ledger_transactions WHERE kind = 'Deposit'")
balances=$(ledger_sql "SELECT coalesce(sum(balance), 0) FROM accounts")
negative=$(ledger_sql "SELECT count(*) FROM accounts WHERE balance < 0")
unbalanced=$(ledger_sql "SELECT count(*) FROM (SELECT transaction_id FROM ledger_entries e JOIN ledger_transactions t ON t.id = e.transaction_id WHERE t.kind = 'Transfer' GROUP BY transaction_id HAVING sum(e.amount) <> 0) x")
duplicates=$(processor_sql "SELECT count(*) - count(DISTINCT transaction_id) FROM processed_messages")
dlq=$(queue_depth processor.transactions.dlq)
ledger=$(ledger_transfers)
processor=$(processor_transfers)

ok=true
row() { # descrição, esperado, obtido
  local status="✅"
  if [[ "$2" != "$3" ]]; then status="❌"; ok=false; fi
  echo "| $1 | $2 | $3 | $status |"
}

{
  echo "### Correção sob carga"
  echo
  echo "Conferido direto nos bancos depois de o outbox e o processor esvaziarem (levou **${drain}s** após o fim da carga)."
  echo
  echo "| Verificação | Esperado | Obtido | |"
  echo "|---|---|---|---|"
  row "Soma dos saldos = soma dos depósitos (dinheiro conservado)" "$deposits" "$balances"
  row "Contas com saldo negativo" "0" "$negative"
  row "Transferências com débito e crédito que não somam zero" "0" "$unbalanced"
  row "Transferências no ledger = no consolidado (quantidade/valor)" "$ledger" "$processor"
  row "Transação contada duas vezes no processor" "0" "$duplicates"
  row "Mensagens na DLQ" "0" "${dlq:-0}"
  echo
} | tee "$RESULTS_DIR/consistency.md"

$ok || { echo "FALHOU: alguma verificação de consistência não bateu" >&2; exit 1; }
