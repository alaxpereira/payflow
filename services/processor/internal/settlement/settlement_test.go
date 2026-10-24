package settlement

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"testing"
	"time"
)

const validEvent = `{
	"transactionId": "0192d6a1-7c3e-7a10-8000-000000000001",
	"kind": "Transfer",
	"sourceAccountId": "0192d6a1-7c3e-7a10-8000-000000000002",
	"destinationAccountId": "0192d6a1-7c3e-7a10-8000-000000000003",
	"amount": 2500,
	"currency": "BRL",
	"occurredAt": "2026-10-24T23:30:00-03:00"
}`

func TestParseTransactionCompleted_EventoValido(t *testing.T) {
	event, err := ParseTransactionCompleted([]byte(validEvent))
	if err != nil {
		t.Fatalf("erro inesperado: %v", err)
	}

	if event.Kind != KindTransfer || event.Amount != 2500 || event.Currency != "BRL" {
		t.Errorf("evento decodificado errado: %+v", event)
	}
	if event.SourceAccountID == nil {
		t.Error("sourceAccountId deveria estar preenchido")
	}

	// 23:30 em Brasília já é dia 25 em UTC.
	if got, want := event.SettlementDate(), time.Date(2026, 10, 25, 0, 0, 0, 0, time.UTC); !got.Equal(want) {
		t.Errorf("SettlementDate = %v, esperado %v", got, want)
	}
}

func TestParseTransactionCompleted_EventosInvalidos(t *testing.T) {
	cases := map[string]string{
		"json malformado":   `{"transactionId":`,
		"sem transactionId": `{"kind":"Deposit","destinationAccountId":"0192d6a1-7c3e-7a10-8000-000000000003","amount":1,"currency":"BRL","occurredAt":"2026-10-24T12:00:00Z"}`,
		"kind desconhecido": `{"transactionId":"0192d6a1-7c3e-7a10-8000-000000000001","kind":"Refund","amount":1,"currency":"BRL","occurredAt":"2026-10-24T12:00:00Z"}`,
		"amount zero":       `{"transactionId":"0192d6a1-7c3e-7a10-8000-000000000001","kind":"Deposit","amount":0,"currency":"BRL","occurredAt":"2026-10-24T12:00:00Z"}`,
		"currency inválida": `{"transactionId":"0192d6a1-7c3e-7a10-8000-000000000001","kind":"Deposit","amount":1,"currency":"REAL","occurredAt":"2026-10-24T12:00:00Z"}`,
		"sem occurredAt":    `{"transactionId":"0192d6a1-7c3e-7a10-8000-000000000001","kind":"Deposit","amount":1,"currency":"BRL"}`,
	}

	for name, body := range cases {
		t.Run(name, func(t *testing.T) {
			_, err := ParseTransactionCompleted([]byte(body))
			if !errors.Is(err, ErrInvalidEvent) {
				t.Errorf("esperado ErrInvalidEvent, veio %v", err)
			}
		})
	}
}

type fakeStore struct {
	processed map[string]bool
	err       error
}

func (s *fakeStore) ApplyTransaction(_ context.Context, messageID string, _ TransactionCompleted) (bool, error) {
	if s.err != nil {
		return false, s.err
	}
	if s.processed[messageID] {
		return false, nil
	}
	s.processed[messageID] = true
	return true, nil
}

func newTestHandler(store Store) *Handler {
	return NewHandler(store, slog.New(slog.NewTextHandler(io.Discard, nil)))
}

func TestHandler_MensagemDuplicadaNaoEErro(t *testing.T) {
	store := &fakeStore{processed: map[string]bool{}}
	handler := newTestHandler(store)

	for range 2 {
		if err := handler.Handle(context.Background(), "msg-1", []byte(validEvent)); err != nil {
			t.Fatalf("erro inesperado: %v", err)
		}
	}
	if len(store.processed) != 1 {
		t.Errorf("esperado 1 mensagem processada, veio %d", len(store.processed))
	}
}

func TestHandler_SemMessageIDEInvalido(t *testing.T) {
	err := newTestHandler(&fakeStore{processed: map[string]bool{}}).Handle(context.Background(), "", []byte(validEvent))

	if !errors.Is(err, ErrInvalidEvent) {
		t.Errorf("esperado ErrInvalidEvent, veio %v", err)
	}
}

func TestHandler_FalhaDoBancoNaoEErroPermanente(t *testing.T) {
	dbErr := errors.New("conexão recusada")

	err := newTestHandler(&fakeStore{err: dbErr}).Handle(context.Background(), "msg-1", []byte(validEvent))

	if !errors.Is(err, dbErr) {
		t.Errorf("o erro original deveria ser preservado, veio %v", err)
	}
	if errors.Is(err, ErrInvalidEvent) {
		t.Error("falha de infraestrutura não pode ser tratada como evento inválido")
	}
}
