// Package postgres implementa a persistência do processor no PostgreSQL.
package postgres

import (
	"context"
	_ "embed"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/alaxpereira/payflow/services/processor/internal/settlement"
)

//go:embed schema.sql
var schema string

type Store struct {
	pool *pgxpool.Pool
}

func New(ctx context.Context, databaseURL string) (*Store, error) {
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		return nil, fmt.Errorf("criando pool: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("conectando ao banco: %w", err)
	}
	return &Store{pool: pool}, nil
}

// Migrate cria as tabelas se ainda não existirem. O schema é pequeno e só cresce, então dispensa uma
// ferramenta de migração por enquanto.
func (s *Store) Migrate(ctx context.Context) error {
	if _, err := s.pool.Exec(ctx, schema); err != nil {
		return fmt.Errorf("aplicando schema: %w", err)
	}
	return nil
}

func (s *Store) Ping(ctx context.Context) error {
	return s.pool.Ping(ctx)
}

func (s *Store) Close() {
	s.pool.Close()
}

// ApplyTransaction implementa o padrão inbox: registrar a mensagem e atualizar o consolidado na mesma
// transação garante que cada mensagem é contada exatamente uma vez, mesmo com entregas duplicadas.
func (s *Store) ApplyTransaction(ctx context.Context, messageID string, event settlement.TransactionCompleted) (bool, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return false, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck // no-op depois do commit

	tag, err := tx.Exec(ctx,
		`INSERT INTO processed_messages (message_id, transaction_id) VALUES ($1, $2)
		 ON CONFLICT (message_id) DO NOTHING`,
		messageID, event.TransactionID)
	if err != nil {
		return false, fmt.Errorf("registrando mensagem: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return false, nil
	}

	_, err = tx.Exec(ctx,
		`INSERT INTO daily_settlements (settlement_date, currency, kind, transaction_count, total_amount)
		 VALUES ($1, $2, $3, 1, $4)
		 ON CONFLICT (settlement_date, currency, kind) DO UPDATE
		 SET transaction_count = daily_settlements.transaction_count + 1,
		     total_amount      = daily_settlements.total_amount + EXCLUDED.total_amount`,
		event.SettlementDate(), event.Currency, string(event.Kind), event.Amount)
	if err != nil {
		return false, fmt.Errorf("atualizando consolidado: %w", err)
	}

	if err := tx.Commit(ctx); err != nil {
		return false, fmt.Errorf("commit: %w", err)
	}
	return true, nil
}

func (s *Store) DailySettlements(ctx context.Context, date time.Time) ([]settlement.DailySettlement, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT currency, kind, transaction_count, total_amount
		 FROM daily_settlements
		 WHERE settlement_date = $1
		 ORDER BY currency, kind`,
		date)
	if err != nil {
		return nil, err
	}

	day := date.Format(time.DateOnly)
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (settlement.DailySettlement, error) {
		s := settlement.DailySettlement{Date: day}
		err := row.Scan(&s.Currency, &s.Kind, &s.TransactionCount, &s.TotalAmount)
		return s, err
	})
}
