using System.Net;
using System.Net.Http.Json;

namespace PayFlow.Ledger.IntegrationTests;

[Collection(LedgerApiCollection.Name)]
public class LedgerApiTests(LedgerApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Deposito_repetido_com_mesma_chave_e_aplicado_uma_vez()
    {
        var account = await OpenAccountAsync();
        var key = NewKey();

        var first = await PostWithKeyAsync($"/accounts/{account.Id}/deposits", new { amount = 5_000 }, key);
        var retry = await PostWithKeyAsync($"/accounts/{account.Id}/deposits", new { amount = 5_000 }, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(5_000, (await GetAccountAsync(account.Id)).Balance);
    }

    [Fact]
    public async Task Requisicao_sem_chave_de_idempotencia_e_rejeitada()
    {
        var account = await OpenAccountAsync();

        var response = await _client.PostAsJsonAsync($"/accounts/{account.Id}/deposits", new { amount = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transferencia_sem_saldo_retorna_422()
    {
        var source = await OpenAccountAsync();
        var destination = await OpenAccountAsync();

        var response = await PostWithKeyAsync("/transfers",
            new { sourceAccountId = source.Id, destinationAccountId = destination.Id, amount = 1 }, NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Transferencias_simultaneas_nunca_deixam_a_conta_de_origem_negativa()
    {
        const int initialBalance = 10_000;
        const int transferAmount = 1_000;
        const int attempts = 25;

        var source = await OpenAccountAsync();
        var destination = await OpenAccountAsync();
        await PostWithKeyAsync($"/accounts/{source.Id}/deposits", new { amount = initialBalance }, NewKey());

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            PostWithKeyAsync("/transfers",
                new { sourceAccountId = source.Id, destinationAccountId = destination.Id, amount = transferAmount },
                NewKey())));

        Assert.Equal(initialBalance / transferAmount, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity }));
        Assert.Equal(0, (await GetAccountAsync(source.Id)).Balance);
        Assert.Equal(initialBalance, (await GetAccountAsync(destination.Id)).Balance);
    }

    [Fact]
    public async Task Transferencias_opostas_simultaneas_nao_causam_deadlock()
    {
        var a = await OpenAccountAsync();
        var b = await OpenAccountAsync();
        await PostWithKeyAsync($"/accounts/{a.Id}/deposits", new { amount = 10_000 }, NewKey());
        await PostWithKeyAsync($"/accounts/{b.Id}/deposits", new { amount = 10_000 }, NewKey());

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
        {
            var (from, to) = i % 2 == 0 ? (a.Id, b.Id) : (b.Id, a.Id);
            return PostWithKeyAsync("/transfers", new { sourceAccountId = from, destinationAccountId = to, amount = 100 }, NewKey());
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(20_000, (await GetAccountAsync(a.Id)).Balance + (await GetAccountAsync(b.Id)).Balance);
    }

    private static string NewKey() => Guid.NewGuid().ToString();

    private async Task<AccountDto> OpenAccountAsync()
    {
        var response = await _client.PostAsJsonAsync("/accounts", new { ownerName = "Titular de Teste", currency = "BRL" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<AccountDto> GetAccountAsync(Guid id) =>
        (await _client.GetFromJsonAsync<AccountDto>($"/accounts/{id}"))!;

    private Task<HttpResponseMessage> PostWithKeyAsync(string url, object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private sealed record AccountDto(Guid Id, long Balance);
}
