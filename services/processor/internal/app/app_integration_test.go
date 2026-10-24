//go:build integration

package app

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"testing"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
	"github.com/testcontainers/testcontainers-go"
	tcpostgres "github.com/testcontainers/testcontainers-go/modules/postgres"
	tcrabbitmq "github.com/testcontainers/testcontainers-go/modules/rabbitmq"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/propagation"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
	"go.opentelemetry.io/otel/trace"

	"github.com/alaxpereira/payflow/services/processor/internal/messaging"
)

// Sobe PostgreSQL e RabbitMQ reais e roda o processor inteiro, publicando mensagens como o ledger publicaria.
func TestProcessor(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()

	// Guarda os spans em memória para conferir a propagação do trace.
	spans := tracetest.NewSpanRecorder()
	otel.SetTracerProvider(sdktrace.NewTracerProvider(sdktrace.WithSpanProcessor(spans)))
	otel.SetTextMapPropagator(propagation.TraceContext{})

	pg, err := tcpostgres.Run(ctx, "postgres:17-alpine",
		tcpostgres.WithDatabase("processor"),
		tcpostgres.WithUsername("payflow"),
		tcpostgres.WithPassword("payflow"),
		tcpostgres.BasicWaitStrategies(),
	)
	testcontainers.CleanupContainer(t, pg)
	if err != nil {
		t.Fatalf("subindo postgres: %v", err)
	}
	databaseURL, err := pg.ConnectionString(ctx, "sslmode=disable")
	if err != nil {
		t.Fatal(err)
	}

	rmq, err := tcrabbitmq.Run(ctx, "rabbitmq:4-alpine")
	testcontainers.CleanupContainer(t, rmq)
	if err != nil {
		t.Fatalf("subindo rabbitmq: %v", err)
	}
	amqpURL, err := rmq.AmqpURL(ctx)
	if err != nil {
		t.Fatal(err)
	}

	cfg := Config{
		DatabaseURL: databaseURL,
		RabbitMQURL: amqpURL,
		HTTPAddr:    freeAddr(t),
		Retry:       messaging.RetryPolicy{Delays: []time.Duration{100 * time.Millisecond, 200 * time.Millisecond}},
	}

	appCtx, stopApp := context.WithCancel(ctx)
	appDone := make(chan error, 1)
	go func() { appDone <- Run(appCtx, cfg, slog.New(slog.NewTextHandler(io.Discard, nil))) }()
	t.Cleanup(func() {
		stopApp()
		if err := <-appDone; err != nil {
			t.Errorf("processor terminou com erro: %v", err)
		}
	})

	baseURL := "http://" + cfg.HTTPAddr
	waitUntil(t, 30*time.Second, func() bool {
		resp, err := http.Get(baseURL + "/health")
		if err != nil {
			return false
		}
		resp.Body.Close()
		return resp.StatusCode == http.StatusOK
	})

	conn, err := amqp.Dial(amqpURL)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	ch, err := conn.Channel()
	if err != nil {
		t.Fatal(err)
	}
	topology := messaging.DefaultTopology(cfg.Retry)
	if err := topology.Declare(ch); err != nil {
		t.Fatal(err)
	}

	publishWithHeaders := func(messageID, body string, headers amqp.Table) {
		t.Helper()
		err := ch.PublishWithContext(ctx, messaging.Exchange, topology.RoutingKey, false, false, amqp.Publishing{
			Headers:     headers,
			MessageId:   messageID,
			Type:        "TransactionCompleted",
			ContentType: "application/json",
			Body:        []byte(body),
		})
		if err != nil {
			t.Fatalf("publicando: %v", err)
		}
	}
	publish := func(messageID, body string) { publishWithHeaders(messageID, body, nil) }

	t.Run("consolida transacoes e ignora entrega duplicada", func(t *testing.T) {
		publish("msg-1", event("0192d6a1-7c3e-7a10-8000-000000000001", "Deposit", 10_000, "2026-10-24T10:00:00Z"))
		publish("msg-2", event("0192d6a1-7c3e-7a10-8000-000000000002", "Deposit", 5_000, "2026-10-24T11:00:00Z"))
		publish("msg-2", event("0192d6a1-7c3e-7a10-8000-000000000002", "Deposit", 5_000, "2026-10-24T11:00:00Z")) // duplicada

		waitUntil(t, 15*time.Second, func() bool {
			s := settlementsOf(t, baseURL, "2026-10-24")
			return len(s) == 1 && s[0].TransactionCount == 2
		})

		s := settlementsOf(t, baseURL, "2026-10-24")
		if s[0].TotalAmount != 15_000 || s[0].Kind != "Deposit" || s[0].Currency != "BRL" {
			t.Errorf("consolidado inesperado: %+v", s[0])
		}
	})

	t.Run("continua o trace que veio no header traceparent", func(t *testing.T) {
		const traceID = "4bf92f3577b34da6a3ce929d0e0e4736"
		const parentSpanID = "00f067aa0ba902b7"

		publishWithHeaders("msg-trace",
			event("0192d6a1-7c3e-7a10-8000-000000000010", "Transfer", 700, "2026-10-26T10:00:00Z"),
			amqp.Table{"traceparent": "00-" + traceID + "-" + parentSpanID + "-01"})

		var consumer sdktrace.ReadOnlySpan
		waitUntil(t, 15*time.Second, func() bool {
			for _, span := range spans.Ended() {
				if span.SpanKind() == trace.SpanKindConsumer && span.SpanContext().TraceID().String() == traceID {
					consumer = span
					return true
				}
			}
			return false
		})

		if got := consumer.Parent().SpanID().String(); got != parentSpanID {
			t.Errorf("span de consumo deveria ser filho de %s, veio %s", parentSpanID, got)
		}
		if consumer.Name() != topology.Queue+" process" {
			t.Errorf("nome do span = %q", consumer.Name())
		}

		// As queries ao banco aparecem como filhas do span de consumo, no mesmo trace.
		var dbSpans int
		for _, span := range spans.Ended() {
			if span.Parent().SpanID() == consumer.SpanContext().SpanID() && span.SpanKind() == trace.SpanKindClient {
				dbSpans++
			}
		}
		if dbSpans == 0 {
			t.Error("esperado ao menos um span de query do banco filho do span de consumo")
		}
	})

	t.Run("mensagem invalida vai direto para a DLQ", func(t *testing.T) {
		publish("msg-invalida", `{"transactionId": "não é um uuid"}`)

		var dead amqp.Delivery
		waitUntil(t, 15*time.Second, func() bool {
			msg, ok, err := ch.Get(topology.DeadLetterQueue(), true)
			if err != nil {
				t.Fatal(err)
			}
			dead = msg
			return ok
		})

		if dead.MessageId != "msg-invalida" {
			t.Errorf("MessageId na DLQ = %q", dead.MessageId)
		}
		if dead.Headers["x-retry-count"] != int32(0) {
			t.Errorf("erro permanente não deveria ter retentativas, x-retry-count = %v", dead.Headers["x-retry-count"])
		}
		if dead.Headers["x-last-error"] == nil {
			t.Error("a DLQ deveria registrar o motivo em x-last-error")
		}
	})
}

type settlementRow struct {
	Currency         string `json:"currency"`
	Kind             string `json:"kind"`
	TransactionCount int64  `json:"transactionCount"`
	TotalAmount      int64  `json:"totalAmount"`
}

func settlementsOf(t *testing.T, baseURL, date string) []settlementRow {
	t.Helper()
	resp, err := http.Get(baseURL + "/settlements?date=" + date)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()

	var body struct {
		Settlements []settlementRow `json:"settlements"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatal(err)
	}
	return body.Settlements
}

func event(transactionID, kind string, amount int64, occurredAt string) string {
	return fmt.Sprintf(`{"transactionId":%q,"kind":%q,"sourceAccountId":null,"destinationAccountId":"0192d6a1-7c3e-7a10-8000-0000000000ff","amount":%d,"currency":"BRL","occurredAt":%q}`,
		transactionID, kind, amount, occurredAt)
}

func freeAddr(t *testing.T) string {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	return l.Addr().String()
}

func waitUntil(t *testing.T, timeout time.Duration, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if condition() {
			return
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatalf("condição não atendida em %s", timeout)
}
