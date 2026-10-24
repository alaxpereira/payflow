using PayFlow.Ledger.Domain;

namespace PayFlow.Ledger.UnitTests.Domain;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Abrir_cria_conta_zerada_e_normaliza_os_dados()
    {
        var result = Account.Open("  Maria Silva ", "brl", Now);

        Assert.True(result.IsSuccess);
        Assert.Equal("Maria Silva", result.Value.OwnerName);
        Assert.Equal("BRL", result.Value.Currency);
        Assert.Equal(0, result.Value.Balance);
        Assert.IsType<AccountOpened>(Assert.Single(result.Value.DomainEvents));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Abrir_exige_nome_do_titular(string? ownerName)
    {
        var result = Account.Open(ownerName, "BRL", Now);

        Assert.Equal(LedgerErrors.OwnerNameRequired, result.Error);
    }

    [Fact]
    public void Abrir_rejeita_nome_do_titular_muito_longo()
    {
        var result = Account.Open(new string('a', Account.MaxOwnerNameLength + 1), "BRL", Now);

        Assert.Equal(LedgerErrors.OwnerNameTooLong, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("XYZ")]
    public void Abrir_rejeita_moeda_nao_suportada(string? currency)
    {
        var result = Account.Open("Maria", currency, Now);

        Assert.Equal("account.unsupported_currency", result.Error?.Code);
    }
}
