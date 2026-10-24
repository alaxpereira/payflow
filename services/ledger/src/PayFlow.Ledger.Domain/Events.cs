using PayFlow.Ledger.Domain.Common;

namespace PayFlow.Ledger.Domain;

public sealed record AccountOpened(Guid AccountId, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record TransactionCompleted(
    Guid TransactionId,
    TransactionKind Kind,
    Guid? SourceAccountId,
    Guid DestinationAccountId,
    long Amount,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;
