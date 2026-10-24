-- Inbox: mensagens já processadas, para descartar entregas duplicadas.
CREATE TABLE IF NOT EXISTS processed_messages (
    message_id     text        PRIMARY KEY,
    transaction_id uuid        NOT NULL,
    processed_at   timestamptz NOT NULL DEFAULT now()
);

-- Consolidado diário por moeda e tipo de transação. Valores em centavos.
CREATE TABLE IF NOT EXISTS daily_settlements (
    settlement_date   date    NOT NULL,
    currency          char(3) NOT NULL,
    kind              text    NOT NULL,
    transaction_count bigint  NOT NULL CHECK (transaction_count > 0),
    total_amount      bigint  NOT NULL CHECK (total_amount > 0),
    PRIMARY KEY (settlement_date, currency, kind)
);
