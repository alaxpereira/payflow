package messaging

import (
	"errors"
	"fmt"
	"testing"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

func TestDecide(t *testing.T) {
	policy := RetryPolicy{Delays: []time.Duration{time.Second, 10 * time.Second}}
	transient := errors.New("banco fora do ar")

	cases := []struct {
		name    string
		err     error
		retries int
		want    Decision
	}{
		{"sucesso confirma", nil, 0, Decision{Action: ActionAck}},
		{"primeira falha espera o primeiro atraso", transient, 0, Decision{Action: ActionRetry, Retries: 1, Delay: time.Second}},
		{"segunda falha espera o segundo atraso", transient, 1, Decision{Action: ActionRetry, Retries: 2, Delay: 10 * time.Second}},
		{"atrasos esgotados vai para a DLQ", transient, 2, Decision{Action: ActionDeadLetter, Retries: 2}},
		{"erro permanente vai direto para a DLQ", Permanent(transient), 0, Decision{Action: ActionDeadLetter}},
		{"erro permanente embrulhado continua permanente", fmt.Errorf("contexto: %w", Permanent(transient)), 0, Decision{Action: ActionDeadLetter}},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := policy.Decide(tc.err, tc.retries); got != tc.want {
				t.Errorf("Decide() = %+v, esperado %+v", got, tc.want)
			}
		})
	}
}

func TestPermanentPreservaOErroOriginal(t *testing.T) {
	original := errors.New("json inválido")

	if !errors.Is(Permanent(original), original) {
		t.Error("errors.Is deveria encontrar o erro original")
	}
	if Permanent(nil) != nil {
		t.Error("Permanent(nil) deveria ser nil")
	}
}

func TestRetryCountAceitaOsTiposInteirosDoAMQP(t *testing.T) {
	cases := []struct {
		headers amqp.Table
		want    int
	}{
		{nil, 0},
		{amqp.Table{}, 0},
		{amqp.Table{retryCountHeader: int32(2)}, 2},
		{amqp.Table{retryCountHeader: int64(3)}, 3},
		{amqp.Table{retryCountHeader: "lixo"}, 0},
	}

	for _, tc := range cases {
		if got := retryCount(tc.headers); got != tc.want {
			t.Errorf("retryCount(%v) = %d, esperado %d", tc.headers, got, tc.want)
		}
	}
}

func TestNomesDasFilas(t *testing.T) {
	topology := DefaultTopology(DefaultRetryPolicy())

	if got := topology.RetryQueue(10 * time.Second); got != "processor.transactions.retry.10000ms" {
		t.Errorf("RetryQueue = %q", got)
	}
	if got := topology.DeadLetterQueue(); got != "processor.transactions.dlq" {
		t.Errorf("DeadLetterQueue = %q", got)
	}
}
