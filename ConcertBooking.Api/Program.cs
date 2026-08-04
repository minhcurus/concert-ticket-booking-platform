using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    // AbortOnConnectFail defaults to true, which makes Connect() throw immediately if Redis is
    // unreachable at startup — since this is a singleton factory, that failure isn't cached and
    // every subsequent request would retry and fail the same way, making the whole API unusable
    // on a cold start with Redis down. Disabling it lets Connect() return a disconnected
    // multiplexer that keeps retrying in the background; callers (RedisIdempotencyCache,
    // CatalogService) already degrade to Postgres when a command against it fails.
    var options = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")!);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<IIdempotencyCache, RedisIdempotencyCache>();

builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<IOperatorConcertService, OperatorConcertService>();
builder.Services.AddScoped<IOperatorBookingService, OperatorBookingService>();
builder.Services.AddHostedService<HoldExpirySweepService>();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("booking", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers["X-User-Id"].ToString(),
            // Read lazily (per partition, i.e. per distinct caller) rather than once at
            // startup: WebApplicationFactory layers test-host configuration overrides onto
            // the builder right before Build() completes, so an eager read here would miss
            // them and always see the production default.
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:Booking:PermitLimit", 10),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many booking requests, try again shortly" }, ct);
    };
});

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CurrentUserMiddleware>();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
}

app.Run();

public partial class Program { }
