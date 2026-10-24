// Package settlement mantém o consolidado diário das transações concluídas no ledger.
package settlement

import (
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
)

type Kind string

const (
	KindDeposit  Kind = "Deposit"
	KindTransfer Kind = "Transfer"
)

// ErrInvalidEvent indica uma mensagem que nunca vai poder ser processada, por mais que seja reenviada.
var ErrInvalidEvent = errors.New("evento inválido")

// TransactionCompleted espelha o evento publicado pelo ledger (contrato em docs/mensageria.md).
type TransactionCompleted struct {
	TransactionID        uuid.UUID  `json:"transactionId"`
	Kind                 Kind       `json:"kind"`
	SourceAccountID      *uuid.UUID `json:"sourceAccountId"`
	DestinationAccountID uuid.UUID  `json:"destinationAccountId"`
	Amount               int64      `json:"amount"`
	Currency             string     `json:"currency"`
	OccurredAt           time.Time  `json:"occurredAt"`
}

// ParseTransactionCompleted decodifica e valida o corpo da mensagem.
func ParseTransactionCompleted(body []byte) (TransactionCompleted, error) {
	var event TransactionCompleted
	if err := json.Unmarshal(body, &event); err != nil {
		return TransactionCompleted{}, fmt.Errorf("%w: JSON malformado: %v", ErrInvalidEvent, err)
	}
	if err := event.validate(); err != nil {
		return TransactionCompleted{}, err
	}
	return event, nil
}

func (e TransactionCompleted) validate() error {
	switch {
	case e.TransactionID == uuid.Nil:
		return fmt.Errorf("%w: transactionId ausente", ErrInvalidEvent)
	case e.Kind != KindDeposit && e.Kind != KindTransfer:
		return fmt.Errorf("%w: kind desconhecido %q", ErrInvalidEvent, e.Kind)
	case e.Amount <= 0:
		return fmt.Errorf("%w: amount deve ser positivo, veio %d", ErrInvalidEvent, e.Amount)
	case len(e.Currency) != 3:
		return fmt.Errorf("%w: currency inválida %q", ErrInvalidEvent, e.Currency)
	case e.OccurredAt.IsZero():
		return fmt.Errorf("%w: occurredAt ausente", ErrInvalidEvent)
	}
	return nil
}

// SettlementDate é o dia (UTC) em que a transação entra no consolidado.
func (e TransactionCompleted) SettlementDate() time.Time {
	y, m, d := e.OccurredAt.UTC().Date()
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC)
}

// DailySettlement é uma linha do consolidado: totais de um dia por moeda e tipo de transação.
type DailySettlement struct {
	Date             string `json:"date"`
	Currency         string `json:"currency"`
	Kind             Kind   `json:"kind"`
	TransactionCount int64  `json:"transactionCount"`
	TotalAmount      int64  `json:"totalAmount"`
}
