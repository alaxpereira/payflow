// Package app monta e executa o processor: banco, consumidor do RabbitMQ e servidor HTTP.
package app

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"strings"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"

	"github.com/alaxpereira/payflow/services/processor/internal/httpapi"
	"github.com/alaxpereira/payflow/services/processor/internal/messaging"
	"github.com/alaxpereira/payflow/services/processor/internal/settlement"
	"github.com/alaxpereira/payflow/services/processor/internal/storage/postgres"
)

type Config struct {
	DatabaseURL string
	RabbitMQURL string
	HTTPAddr    string
	Retry       messaging.RetryPolicy
}

// ConfigFromEnv lê PROCESSOR_DATABASE_URL, PROCESSOR_RABBITMQ_URL, PROCESSOR_HTTP_ADDR e
// PROCESSOR_RETRY_DELAYS (ex.: "1s,10s,1m").
func ConfigFromEnv() (Config, error) {
	cfg := Config{
		DatabaseURL: os.Getenv("PROCESSOR_DATABASE_URL"),
		RabbitMQURL: os.Getenv("PROCESSOR_RABBITMQ_URL"),
		HTTPAddr:    os.Getenv("PROCESSOR_HTTP_ADDR"),
		Retry:       messaging.DefaultRetryPolicy(),
	}

	var missing []string
	if cfg.DatabaseURL == "" {
		missing = append(missing, "PROCESSOR_DATABASE_URL")
	}
	if cfg.RabbitMQURL == "" {
		missing = append(missing, "PROCESSOR_RABBITMQ_URL")
	}
	if len(missing) > 0 {
		return Config{}, fmt.Errorf("variáveis de ambiente obrigatórias ausentes: %s", strings.Join(missing, ", "))
	}

	if cfg.HTTPAddr == "" {
		cfg.HTTPAddr = ":8081"
	}

	if raw := os.Getenv("PROCESSOR_RETRY_DELAYS"); raw != "" {
		delays, err := parseDelays(raw)
		if err != nil {
			return Config{}, err
		}
		cfg.Retry = messaging.RetryPolicy{Delays: delays}
	}
	return cfg, nil
}

func parseDelays(raw string) ([]time.Duration, error) {
	var delays []time.Duration
	for _, part := range strings.Split(raw, ",") {
		d, err := time.ParseDuration(strings.TrimSpace(part))
		if err != nil || d <= 0 {
			return nil, fmt.Errorf("PROCESSOR_RETRY_DELAYS inválido: %q", part)
		}
		delays = append(delays, d)
	}
	return delays, nil
}

// Run executa o processor até ctx ser cancelado e então desliga de forma ordenada.
func Run(ctx context.Context, cfg Config, log *slog.Logger) error {
	store, err := postgres.New(ctx, cfg.DatabaseURL)
	if err != nil {
		return err
	}
	defer store.Close()

	if err := store.Migrate(ctx); err != nil {
		return err
	}

	handler := settlement.NewHandler(store, log)
	consumer := messaging.NewConsumer(
		cfg.RabbitMQURL,
		messaging.DefaultTopology(cfg.Retry),
		func(ctx context.Context, d amqp.Delivery) error {
			err := handler.Handle(ctx, d.MessageId, d.Body)
			if errors.Is(err, settlement.ErrInvalidEvent) {
				return messaging.Permanent(err)
			}
			return err
		},
		messaging.ConsumerOptions{},
		log,
	)
	server := httpapi.NewServer(cfg.HTTPAddr, store, log)

	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	consumerDone := make(chan struct{})
	go func() {
		defer close(consumerDone)
		_ = consumer.Run(ctx) // só retorna quando ctx é cancelado
	}()

	serverErr := make(chan error, 1)
	go func() {
		if err := server.ListenAndServe(); !errors.Is(err, http.ErrServerClosed) {
			serverErr <- err
		}
		close(serverErr)
	}()

	log.Info("processor iniciado", "http", cfg.HTTPAddr)

	var runErr error
	select {
	case <-ctx.Done():
	case err := <-serverErr:
		runErr = fmt.Errorf("servidor HTTP: %w", err)
	}

	log.Info("desligando")
	cancel()

	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	if err := server.Shutdown(shutdownCtx); err != nil && runErr == nil {
		runErr = err
	}
	<-consumerDone
	return runErr
}
