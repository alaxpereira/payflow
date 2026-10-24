package settlement

import (
	"context"
	"fmt"
	"log/slog"
)

// Store persiste o efeito de um evento.
type Store interface {
	// ApplyTransaction soma a transação no consolidado e registra messageID na mesma transação de banco.
	// Retorna applied=false quando messageID já tinha sido processado (entrega duplicada).
	ApplyTransaction(ctx context.Context, messageID string, event TransactionCompleted) (applied bool, err error)
}

// Handler processa mensagens TransactionCompleted. É idempotente: o RabbitMQ entrega at-least-once.
type Handler struct {
	store Store
	log   *slog.Logger
}

func NewHandler(store Store, log *slog.Logger) *Handler {
	return &Handler{store: store, log: log}
}

func (h *Handler) Handle(ctx context.Context, messageID string, body []byte) error {
	if messageID == "" {
		return fmt.Errorf("%w: mensagem sem message_id, impossível deduplicar", ErrInvalidEvent)
	}

	event, err := ParseTransactionCompleted(body)
	if err != nil {
		return err
	}

	applied, err := h.store.ApplyTransaction(ctx, messageID, event)
	if err != nil {
		return fmt.Errorf("aplicando transação %s: %w", event.TransactionID, err)
	}

	if !applied {
		h.log.InfoContext(ctx, "mensagem duplicada ignorada", "message_id", messageID, "transaction_id", event.TransactionID)
		return nil
	}

	h.log.InfoContext(ctx, "transação consolidada",
		"message_id", messageID, "transaction_id", event.TransactionID, "kind", event.Kind, "amount", event.Amount)
	return nil
}
