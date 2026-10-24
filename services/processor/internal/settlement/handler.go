package settlement

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

const instrumentationName = "github.com/alaxpereira/payflow/services/processor/internal/settlement"

// Store persiste o efeito de um evento.
type Store interface {
	// ApplyTransaction soma a transação no consolidado e registra messageID na mesma transação de banco.
	// Retorna applied=false quando messageID já tinha sido processado (entrega duplicada).
	ApplyTransaction(ctx context.Context, messageID string, event TransactionCompleted) (applied bool, err error)
}

// Handler processa mensagens TransactionCompleted. É idempotente: o RabbitMQ entrega at-least-once.
type Handler struct {
	store      Store
	log        *slog.Logger
	duplicates metric.Int64Counter
	lag        metric.Float64Histogram
}

func NewHandler(store Store, log *slog.Logger) *Handler {
	meter := otel.Meter(instrumentationName)

	duplicates, err := meter.Int64Counter("payflow.processor.duplicates",
		metric.WithUnit("{message}"),
		metric.WithDescription("Entregas duplicadas descartadas pelo inbox."))
	if err != nil {
		otel.Handle(err)
	}
	lag, err := meter.Float64Histogram("payflow.processor.end_to_end_lag",
		metric.WithUnit("s"),
		metric.WithDescription("Tempo entre a transação acontecer no ledger e entrar no consolidado."),
		metric.WithExplicitBucketBoundaries(0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120))
	if err != nil {
		otel.Handle(err)
	}

	return &Handler{store: store, log: log, duplicates: duplicates, lag: lag}
}

func (h *Handler) Handle(ctx context.Context, messageID string, body []byte) error {
	if messageID == "" {
		return fmt.Errorf("%w: mensagem sem message_id, impossível deduplicar", ErrInvalidEvent)
	}

	event, err := ParseTransactionCompleted(body)
	if err != nil {
		return err
	}

	span := trace.SpanFromContext(ctx)
	span.SetAttributes(
		attribute.String("payflow.transaction.id", event.TransactionID.String()),
		attribute.String("payflow.transaction.kind", string(event.Kind)),
	)

	applied, err := h.store.ApplyTransaction(ctx, messageID, event)
	if err != nil {
		return fmt.Errorf("aplicando transação %s: %w", event.TransactionID, err)
	}

	span.SetAttributes(attribute.Bool("payflow.duplicate", !applied))
	if !applied {
		h.duplicates.Add(ctx, 1)
		h.log.InfoContext(ctx, "mensagem duplicada ignorada", "message_id", messageID, "transaction_id", event.TransactionID)
		return nil
	}

	h.lag.Record(ctx, time.Since(event.OccurredAt).Seconds(),
		metric.WithAttributes(attribute.String("kind", string(event.Kind))))
	h.log.InfoContext(ctx, "transação consolidada",
		"message_id", messageID, "transaction_id", event.TransactionID, "kind", event.Kind, "amount", event.Amount)
	return nil
}
