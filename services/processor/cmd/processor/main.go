package main

import (
	"context"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/alaxpereira/payflow/services/processor/internal/app"
	"github.com/alaxpereira/payflow/services/processor/internal/telemetry"
)

func main() {
	log := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	slog.SetDefault(log)

	cfg, err := app.ConfigFromEnv()
	if err != nil {
		log.Error("configuração inválida", "erro", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	shutdownTelemetry, err := telemetry.Setup(ctx)
	if err != nil {
		log.Error("falha ao configurar a telemetria", "erro", err)
		os.Exit(1)
	}

	runErr := app.Run(ctx, cfg, log)

	// Envia os últimos spans e métricas antes de sair.
	flushCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := shutdownTelemetry(flushCtx); err != nil {
		log.Warn("falha ao enviar a telemetria pendente", "erro", err)
	}

	if runErr != nil {
		log.Error("processor encerrado com erro", "erro", runErr)
		os.Exit(1)
	}
}
