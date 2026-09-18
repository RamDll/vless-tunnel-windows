using System.Net;
using VlessTunnel.Core;
using Xunit;

namespace VlessTunnel.Core.Tests;

// Через подставной HttpMessageHandler, не реальную сеть (план, стиль
// тестов в этом репозитории — быстрые и детерминированные, а настоящий
// HTTP-запрос в CI/офлайн-песочнице был бы хрупким).
file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

public class ClockCheckTests
{
    [Fact]
    public async Task InSyncClock_ReportsSmallSkew()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        response.Headers.Date = DateTimeOffset.UtcNow;
        using var client = new HttpClient(new StubHandler(_ => response));

        var result = await ClockCheck.CheckAsync(client);

        Assert.True(result.Ok);
        Assert.NotNull(result.SkewSeconds);
        Assert.True(Math.Abs(result.SkewSeconds!.Value) < 5);
    }

    [Fact]
    public async Task SkewedClock_ReportsLargeSkew()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        response.Headers.Date = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        using var client = new HttpClient(new StubHandler(_ => response));

        var result = await ClockCheck.CheckAsync(client);

        Assert.True(result.Ok);
        Assert.True(result.SkewSeconds > 7000); // ~2 часа, с запасом на погрешность
    }

    [Fact]
    public async Task MissingDateHeader_ReportsNotOk()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        using var client = new HttpClient(new StubHandler(_ => response));

        var result = await ClockCheck.CheckAsync(client);

        Assert.False(result.Ok);
        Assert.Null(result.SkewSeconds);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task NetworkFailure_ReportsNotOkWithMessage()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("нет сети")));

        var result = await ClockCheck.CheckAsync(client);

        Assert.False(result.Ok);
        Assert.Contains("нет сети", result.Error);
    }
}
