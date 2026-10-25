# Teste de carga

Resultado do [teste de carga com k6](../loadtest/transfers.js) rodando no GitHub Actions. Além de latência e
vazão, o teste confere, direto nos bancos, que **o sistema continua correto sob carga**.

Para reproduzir: **Actions → loadtest → Run workflow**, escolhendo a taxa e a duração. O resumo aparece no próprio
job e fica como artefato.

## Ambiente

| | |
|---|---|
| Máquina | GitHub Actions `ubuntu-latest`, 4 vCPUs, 15 GB de RAM |
| Topologia | Todos os containers (bancos, RabbitMQ, os dois serviços, observabilidade) **e o próprio k6** na mesma máquina |
| Commit | `1f2dcc3` |
| Data | 2026-10-25 |

Três cenários simultâneos: **`transfers`** (carga principal entre 50 contas), **`hot_accounts`** (transferências
nos dois sentidos entre as mesmas duas contas, o pior caso do `SELECT ... FOR UPDATE`) e **`client_retries`**
(cliente repetindo a requisição com a mesma `Idempotency-Key`).

## Resultado

Patamar de **100 transferências/s por 2 minutos**, mais 30 s de rampa.

| Métrica | Valor |
|---|---|
| Requisições HTTP | 19.052 (118,5/s em média, contando as retentativas) |
| Transferências aceitas | 17.576 (visão do cliente; 17.577 no banco, ver nota abaixo) |
| Recusadas por falta de saldo (422) | 163 |
| Retentativas respondidas pela idempotência | 1.188, todas com a mesma transação da original |
| Erros HTTP inesperados | 0,00% |
| Thresholds | ✅ todos dentro |

| Cenário | Mediana | p90 | p95 | p99 | Máximo |
|---|---|---|---|---|---|
| `transfers` | 3,2 ms | 13,6 ms | 55,1 ms | 152,2 ms | 422,6 ms |
| `hot_accounts` | 4,1 ms | 22,3 ms | 70,6 ms | 179,4 ms | 438,8 ms |
| `client_retries` | 3,4 ms | 8,4 ms | 34,5 ms | 138,7 ms | 386,7 ms |

## Correção sob carga

Conferido nos bancos depois de o outbox e o processor esvaziarem. Isso levou **2 s** após o fim da carga.

| Verificação | Esperado | Obtido | |
|---|---|---|---|
| Soma dos saldos = soma dos depósitos (dinheiro conservado) | 50.000.000 | 50.000.000 | ✅ |
| Contas com saldo negativo | 0 | 0 | ✅ |
| Transferências com débito e crédito que não somam zero | 0 | 0 | ✅ |
| Transferências no ledger = no consolidado (quantidade / valor em centavos) | 17.577 / 441.111.053 | 17.577 / 441.111.053 | ✅ |
| Transação contada duas vezes no processor | 0 | 0 | ✅ |
| Mensagens na DLQ | 0 | 0 | ✅ |

## O que os números dizem

- **A latência típica é de poucos milissegundos.** A mediana fica em 3 a 4 ms. A cauda (p95 de 55 ms, p99 de
  152 ms) vem principalmente de a máquina estar dividida entre o k6, dois bancos, o RabbitMQ, os dois serviços e a
  stack de observabilidade, tudo em 4 vCPUs.
- **A contenção custa pouco.** No `hot_accounts`, todas as transferências disputam o lock das mesmas duas linhas e
  são serializadas. O p95 sobe de 55 para 71 ms, e não houve nenhum deadlock, mesmo com transferências nos dois
  sentidos ao mesmo tempo: travar as contas sempre na mesma ordem (`ORDER BY id`) funciona.
- **A idempotência aguenta carga.** Todas as 1.188 retentativas receberam a transação original, sem mover
  dinheiro de novo.
- **A mensageria acompanha em tempo real.** Dois segundos depois do fim da carga, o outbox estava vazio e o
  consolidado do processor batia com o ledger até o centavo, sem nenhuma duplicata e sem nada na DLQ.
- **Os 422 são esperados.** Com transferências aleatórias, algumas contas ficam sem saldo. É uma resposta de
  negócio, não uma falha.

**Nota sobre a diferença de 1 transferência.** O k6 contou 17.576 transferências aceitas, e o banco tem 17.577.
O mais provável é uma iteração interrompida no fim do teste: a requisição chegou ao servidor e foi gravada, mas o
k6 encerrou antes de registrar a resposta. É exatamente a situação em que um cliente real repetiria a requisição,
e a idempotência devolveria o resultado original. A verificação que importa, ledger contra consolidado, é feita
nos bancos e bate.

## Limites deste teste

- O k6 roda na mesma máquina que o sistema e disputa CPU com ele, e runners do GitHub Actions variam de uma
  execução para outra. Os números servem como referência e para pegar regressões, não como capacidade de produção.
- 100 transferências/s não é o limite do sistema, e este teste não procura o ponto de saturação. Para isso, rode o
  workflow com taxas maiores (200, 400, ...) e observe onde o p95 dispara ou os thresholds estouram.
