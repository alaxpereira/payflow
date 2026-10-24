// Package httpapi expõe o health check e a consulta do consolidado.
package httpapi

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"time"

	"github.com/alaxpereira/payflow/services/processor/internal/settlement"
)

type Store interface {
	Ping(ctx context.Context) error
	DailySettlements(ctx context.Context, date time.Time) ([]settlement.DailySettlement, error)
}

func NewServer(addr string, store Store, log *slog.Logger) *http.Server {
	mux := http.NewServeMux()

	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
		defer cancel()

		if err := store.Ping(ctx); err != nil {
			log.WarnContext(r.Context(), "health check falhou", "erro", err)
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"status": "indisponível"})
			return
		}
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
	})

	// GET /settlements?date=2026-10-24
	mux.HandleFunc("GET /settlements", func(w http.ResponseWriter, r *http.Request) {
		date, err := time.Parse(time.DateOnly, r.URL.Query().Get("date"))
		if err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"erro": "informe date no formato AAAA-MM-DD"})
			return
		}

		settlements, err := store.DailySettlements(r.Context(), date)
		if err != nil {
			log.ErrorContext(r.Context(), "falha ao consultar consolidado", "erro", err)
			writeJSON(w, http.StatusInternalServerError, map[string]string{"erro": "falha ao consultar o consolidado"})
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"date": date.Format(time.DateOnly), "settlements": settlements})
	})

	return &http.Server{Addr: addr, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}
