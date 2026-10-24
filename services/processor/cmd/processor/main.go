package main

import (
	"context"
	"log/slog"
	"os"
	"os/signal"
	"syscall"

	"github.com/alaxpereira/payflow/services/processor/internal/app"
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

	if err := app.Run(ctx, cfg, log); err != nil {
		log.Error("processor encerrado com erro", "erro", err)
		os.Exit(1)
	}
}
