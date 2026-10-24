using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Ledger.Domain;
using PayFlow.Ledger.Infrastructure.Outbox;

namespace PayFlow.Ledger.Infrastructure.Persistence;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", t =>
            t.HasCheckConstraint("ck_accounts_balance_non_negative", "balance >= 0"));

        builder.HasKey(a => a.Id);
        builder.Property(a => a.OwnerName).HasMaxLength(Account.MaxOwnerNameLength);
        builder.Property(a => a.Currency).HasMaxLength(3).IsFixedLength();
        builder.Ignore(a => a.DomainEvents);
    }
}

internal sealed class LedgerTransactionConfiguration : IEntityTypeConfiguration<LedgerTransaction>
{
    public void Configure(EntityTypeBuilder<LedgerTransaction> builder)
    {
        builder.ToTable("ledger_transactions", t =>
            t.HasCheckConstraint("ck_ledger_transactions_amount_positive", "amount > 0"));

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(t => t.IdempotencyKey).HasMaxLength(LedgerTransaction.MaxIdempotencyKeyLength);
        builder.Property(t => t.RequestHash).HasMaxLength(64);
        builder.HasIndex(t => t.IdempotencyKey).IsUnique();

        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Account>().WithMany().HasForeignKey(t => t.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Entries).WithOne().HasForeignKey(e => e.TransactionId);
        builder.Ignore(t => t.DomainEvents);
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(e => e.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);

        // Atende a consulta de extrato (mais recentes primeiro, por conta).
        builder.HasIndex(e => new { e.AccountId, e.CreatedAt });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasMaxLength(200);
        builder.Property(m => m.Payload).HasColumnType("jsonb");

        // O relay só varre mensagens não processadas, em ordem.
        builder.HasIndex(m => m.OccurredAt).HasFilter("processed_at IS NULL");
    }
}
