using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class OperatorConcertWriteTests
{
    private readonly ApiFactory _factory;
    public OperatorConcertWriteTests(ApiFactory factory) => _factory = factory;

    private HttpClient OperatorClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.OperatorId.ToString());
        return client;
    }

    [Fact]
    public async Task CreateUpdatePublish_FullLifecycle_Succeeds()
    {
        var client = OperatorClient();

        var createResponse = await client.PostAsJsonAsync("/api/operator/concerts",
            new CreateConcertRequest("New Show", "Desc", "Venue", DateTimeOffset.UtcNow.AddDays(60)));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ConcertDetailDto>();
        Assert.Equal("Draft", created!.Status);

        var updateResponse = await client.PutAsJsonAsync($"/api/operator/concerts/{created.Id}",
            new UpdateConcertRequest("Renamed Show", "Desc2", "Venue2", DateTimeOffset.UtcNow.AddDays(61)));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ConcertDetailDto>();
        Assert.Equal("Renamed Show", updated!.Title);

        var categoryResponse = await client.PostAsJsonAsync($"/api/operator/concerts/{created.Id}/ticket-categories",
            new AddTicketCategoryRequest("General", 50m, 200));
        Assert.Equal(HttpStatusCode.OK, categoryResponse.StatusCode);

        var publishResponse = await client.PostAsync($"/api/operator/concerts/{created.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<ConcertDetailDto>();
        Assert.Equal("Published", published!.Status);
        Assert.Single(published.TicketCategories);
    }

    [Fact]
    public async Task Update_UnknownConcert_Returns404()
    {
        var response = await OperatorClient().PutAsJsonAsync($"/api/operator/concerts/{Guid.NewGuid()}",
            new UpdateConcertRequest("T", "D", "V", DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
