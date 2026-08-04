using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class OperatorConcertReadTests
{
    private readonly ApiFactory _factory;
    public OperatorConcertReadTests(ApiFactory factory) => _factory = factory;

    private HttpClient OperatorClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.OperatorId.ToString());
        return client;
    }

    [Fact]
    public async Task List_IncludesDraftConcerts()
    {
        var draftId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Concerts.Add(new Concert { Id = draftId, Title = "Unpublished Show", Description = "D", VenueName = "V", StartTime = DateTimeOffset.UtcNow.AddDays(10), Status = ConcertStatus.Draft });
            await db.SaveChangesAsync();
        }

        var response = await OperatorClient().GetAsync("/api/operator/concerts");
        var concerts = await response.Content.ReadFromJsonAsync<List<ConcertDetailDto>>();

        Assert.Contains(concerts!, c => c.Id == draftId);
    }

    [Fact]
    public async Task CustomerRole_Returns403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        var response = await client.GetAsync("/api/operator/concerts");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
