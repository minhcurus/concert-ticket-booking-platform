using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class ConcertsControllerTests
{
    private readonly ApiFactory _factory;
    public ConcertsControllerTests(ApiFactory factory) => _factory = factory;

    private HttpClient CustomerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        return client;
    }

    [Fact]
    public async Task List_ReturnsSeededConcert()
    {
        var response = await CustomerClient().GetAsync("/api/concerts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var concerts = await response.Content.ReadFromJsonAsync<List<ConcertSummaryDto>>();
        Assert.Contains(concerts!, c => c.Id == DbSeeder.ConcertId);
    }

    [Fact]
    public async Task GetById_ReturnsTicketCategories()
    {
        var response = await CustomerClient().GetAsync($"/api/concerts/{DbSeeder.ConcertId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var concert = await response.Content.ReadFromJsonAsync<ConcertDetailDto>();
        Assert.Equal(2, concert!.TicketCategories.Count);
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var response = await CustomerClient().GetAsync($"/api/concerts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    [InlineData(1, 101)]
    public async Task List_InvalidPagingParameters_Returns400(int page, int pageSize)
    {
        var response = await CustomerClient().GetAsync($"/api/concerts?page={page}&pageSize={pageSize}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
