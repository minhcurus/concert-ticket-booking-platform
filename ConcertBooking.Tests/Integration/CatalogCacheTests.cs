using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class CatalogCacheTests
{
    private readonly ApiFactory _factory;
    public CatalogCacheTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_CalledTwice_ReturnsConsistentResults()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());

        var first = await (await client.GetAsync("/api/concerts")).Content.ReadFromJsonAsync<List<ConcertSummaryDto>>();
        var second = await (await client.GetAsync("/api/concerts")).Content.ReadFromJsonAsync<List<ConcertSummaryDto>>();

        Assert.Equal(first!.Select(c => c.Id), second!.Select(c => c.Id));
    }
}
