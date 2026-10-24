package messaging

import (
	"errors"
	"time"
)

// RetryPolicy define os atrasos entre tentativas. Esgotados os atrasos, a mensagem vai para a DLQ.
type RetryPolicy struct {
	Delays []time.Duration
}

func DefaultRetryPolicy() RetryPolicy {
	return RetryPolicy{Delays: []time.Duration{time.Second, 10 * time.Second, time.Minute}}
}

type Action int

const (
	ActionAck Action = iota
	ActionRetry
	ActionDeadLetter
)

func (a Action) String() string {
	switch a {
	case ActionAck:
		return "ack"
	case ActionRetry:
		return "retry"
	case ActionDeadLetter:
		return "dead-letter"
	}
	return "desconhecida"
}

// Outcome é o rótulo da ação nas métricas e traces.
func (a Action) Outcome() string {
	switch a {
	case ActionAck:
		return "processed"
	case ActionRetry:
		return "retried"
	case ActionDeadLetter:
		return "dead_lettered"
	}
	return "unknown"
}

// Decision diz o que fazer com uma mensagem depois de processada.
type Decision struct {
	Action Action
	// Retries é o valor do contador de retentativas a gravar na mensagem republicada.
	Retries int
	Delay   time.Duration
}

// Decide escolhe o destino da mensagem a partir do erro do handler e de quantas retentativas ela já teve.
func (p RetryPolicy) Decide(err error, retries int) Decision {
	switch {
	case err == nil:
		return Decision{Action: ActionAck}
	case IsPermanent(err):
		// Reenviar uma mensagem que nunca vai dar certo só atrasa as outras.
		return Decision{Action: ActionDeadLetter, Retries: retries}
	case retries >= len(p.Delays):
		return Decision{Action: ActionDeadLetter, Retries: retries}
	default:
		return Decision{Action: ActionRetry, Retries: retries + 1, Delay: p.Delays[retries]}
	}
}

type permanentError struct{ err error }

func (e *permanentError) Error() string { return e.err.Error() }
func (e *permanentError) Unwrap() error { return e.err }

// Permanent marca um erro como definitivo: a mensagem vai direto para a DLQ, sem retentativas.
func Permanent(err error) error {
	if err == nil {
		return nil
	}
	return &permanentError{err: err}
}

func IsPermanent(err error) bool {
	var p *permanentError
	return errors.As(err, &p)
}
