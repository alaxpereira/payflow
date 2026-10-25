// Teste de carga do PayFlow com k6.
//
// Três cenários rodando juntos:
//   transfers       carga principal: transferências aleatórias entre muitas contas
//   hot_accounts    contenção: transferências nos dois sentidos entre as mesmas duas contas (pior caso dos locks)
//   client_retries  cliente repetindo a requisição com a mesma Idempotency-Key, como depois de um timeout
//
// A correção sob carga (dinheiro conservado, consolidado batendo com o ledger) é conferida depois, direto nos
// bancos, por scripts/loadtest-verify.sh.
//
// Variáveis: LEDGER_URL, RATE (transferências/s no patamar), DURATION (duração do patamar), ACCOUNTS.

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const LEDGER = __ENV.LEDGER_URL || 'http://localhost:8080';
const RATE = Number(__ENV.RATE || 100);
const DURATION = __ENV.DURATION || '2m';
const ACCOUNTS = Number(__ENV.ACCOUNTS || 50);
const INITIAL_BALANCE = 1_000_000; // R$ 10.000,00 em centavos
const RESULTS_DIR = __ENV.RESULTS_DIR || 'loadtest/results';

const accepted = new Counter('payflow_transfers_accepted');
const rejected = new Counter('payflow_transfers_rejected_insufficient_funds');
const replays = new Counter('payflow_idempotent_replays');

// 422 (sem saldo) é uma resposta de negócio esperada, não um erro do sistema.
http.setResponseCallback(http.expectedStatuses(200, 201, 422));

export const options = {
  scenarios: {
    transfers: {
      executor: 'ramping-arrival-rate',
      exec: 'transfer',
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 400,
      stages: [
        { target: RATE, duration: '30s' },
        { target: RATE, duration: DURATION },
        { target: 0, duration: '10s' },
      ],
    },
    hot_accounts: {
      executor: 'constant-arrival-rate',
      exec: 'hotTransfer',
      rate: Math.max(1, Math.round(RATE / 5)),
      timeUnit: '1s',
      duration: DURATION,
      startTime: '30s',
      preAllocatedVUs: 20,
      maxVUs: 100,
    },
    client_retries: {
      executor: 'constant-arrival-rate',
      exec: 'retriedTransfer',
      rate: Math.max(1, Math.round(RATE / 10)),
      timeUnit: '1s',
      duration: DURATION,
      startTime: '30s',
      preAllocatedVUs: 10,
      maxVUs: 50,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    'http_req_duration{scenario:transfers}': ['p(95)<500'],
    'http_req_duration{scenario:hot_accounts}': ['p(95)<1000'],
    'http_req_duration{scenario:client_retries}': ['p(95)<500'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const json = { headers: { 'Content-Type': 'application/json' } };

function post(path, body, idempotencyKey) {
  const params = { headers: { ...json.headers } };
  if (idempotencyKey) params.headers['Idempotency-Key'] = idempotencyKey;
  return http.post(`${LEDGER}${path}`, JSON.stringify(body), params);
}

export function setup() {
  for (let i = 0; i < 60 && http.get(`${LEDGER}/health`).status !== 200; i++) sleep(1);

  const accounts = [];
  for (let i = 0; i < ACCOUNTS; i++) {
    const res = post('/accounts', { ownerName: `Conta de carga ${i}`, currency: 'BRL' });
    if (res.status !== 201) fail(`não consegui abrir a conta ${i}: ${res.status} ${res.body}`);
    const id = res.json('id');
    const deposit = post(`/accounts/${id}/deposits`, { amount: INITIAL_BALANCE }, `loadtest-seed-${id}`);
    if (deposit.status !== 201) fail(`não consegui depositar na conta ${i}: ${deposit.status}`);
    accounts.push(id);
  }
  return { accounts };
}

function pickTwo(accounts) {
  const a = Math.floor(Math.random() * accounts.length);
  let b = Math.floor(Math.random() * (accounts.length - 1));
  if (b >= a) b++;
  return [accounts[a], accounts[b]];
}

function randomAmount() {
  return 100 + Math.floor(Math.random() * 50_000); // R$ 1,00 a R$ 501,00
}

function sendTransfer(source, destination, key) {
  const res = post('/transfers', { sourceAccountId: source, destinationAccountId: destination, amount: randomAmount() }, key);
  check(res, { 'transferência: 201 ou 422': (r) => r.status === 201 || r.status === 422 });
  if (res.status === 201) accepted.add(1);
  if (res.status === 422) rejected.add(1);
  return res;
}

export function transfer(data) {
  const [source, destination] = pickTwo(data.accounts);
  sendTransfer(source, destination, crypto.randomUUID());
}

export function hotTransfer(data) {
  const [a, b] = [data.accounts[0], data.accounts[1]];
  const [source, destination] = Math.random() < 0.5 ? [a, b] : [b, a];
  sendTransfer(source, destination, crypto.randomUUID());
}

export function retriedTransfer(data) {
  const [source, destination] = pickTwo(data.accounts);
  const key = crypto.randomUUID();
  const body = { sourceAccountId: source, destinationAccountId: destination, amount: randomAmount() };

  const first = post('/transfers', body, key);
  if (first.status === 201) accepted.add(1);
  const retry = post('/transfers', body, key);

  check(retry, {
    'retentativa: devolve a mesma transação': (r) =>
      first.status !== 201 || (r.status === 200 && r.json('id') === first.json('id')),
    'retentativa: marcada como replay': (r) => first.status !== 201 || r.headers['Idempotent-Replayed'] === 'true',
  });
  if (retry.status === 200) replays.add(1);
}

// --- Resumo em Markdown, publicado em docs/benchmark.md -----------------------------------------------------

function ms(value) {
  return value === undefined ? '–' : `${value.toFixed(1)} ms`;
}

function durationRow(data, scenario) {
  const metric = data.metrics[`http_req_duration{scenario:${scenario}}`];
  if (!metric) return `| ${scenario} | – | – | – | – | – |`;
  const v = metric.values;
  return `| \`${scenario}\` | ${ms(v.med)} | ${ms(v['p(90)'])} | ${ms(v['p(95)'])} | ${ms(v['p(99)'])} | ${ms(v.max)} |`;
}

function count(data, name) {
  return data.metrics[name] ? data.metrics[name].values.count : 0;
}

export function handleSummary(data) {
  const reqs = data.metrics.http_reqs.values;
  const failed = data.metrics.http_req_failed.values.rate;
  const thresholdsOk = Object.values(data.metrics).every(
    (m) => !m.thresholds || Object.values(m.thresholds).every((t) => t.ok),
  );

  const markdown = [
    `### Resultado do k6`,
    ``,
    `Patamar de **${RATE} transferências/s** por **${DURATION}** (mais rampa de 30s), ${ACCOUNTS} contas.`,
    ``,
    `| Métrica | Valor |`,
    `|---|---|`,
    `| Requisições HTTP | ${reqs.count} (${reqs.rate.toFixed(1)}/s em média) |`,
    `| Transferências aceitas | ${count(data, 'payflow_transfers_accepted')} |`,
    `| Recusadas por falta de saldo (422) | ${count(data, 'payflow_transfers_rejected_insufficient_funds')} |`,
    `| Retentativas respondidas pela idempotência | ${count(data, 'payflow_idempotent_replays')} |`,
    `| Erros HTTP inesperados | ${(failed * 100).toFixed(2)}% |`,
    `| Thresholds | ${thresholdsOk ? '✅ todos dentro' : '❌ algum estourou'} |`,
    ``,
    `| Cenário | Mediana | p90 | p95 | p99 | Máximo |`,
    `|---|---|---|---|---|---|`,
    durationRow(data, 'transfers'),
    durationRow(data, 'hot_accounts'),
    durationRow(data, 'client_retries'),
    ``,
  ].join('\n');

  return {
    stdout: `\n${markdown}\n`,
    [`${RESULTS_DIR}/k6-summary.md`]: markdown,
    [`${RESULTS_DIR}/k6-summary.json`]: JSON.stringify(data, null, 2),
  };
}
