using System.Net.Http.Json;
using Aspire.Hosting;

namespace OutboxDemo.Tests;

// Starts the whole Aspire app once (SQL Server, Service Bus emulator and the four services) and
// shares it between the tests: starting the containers is the slow part.
public sealed class DemoAppFixture : IAsyncLifetime
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private DistributedApplication? _app;

    public HttpClient Orders { get; private set; } = null!;
    public HttpClient Shipping { get; private set; } = null!;
    public HttpClient Dispatcher { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.OutboxDemo_AppHost>(cts.Token);
        _app = await builder.BuildAsync(cts.Token);
        await _app.StartAsync(cts.Token);

        foreach (var resource in new[] { "orders", "shipping", "dispatcher", "outbox-relay" })
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(resource, cts.Token);

        Orders = _app.CreateHttpClient("orders");
        Shipping = _app.CreateHttpClient("shipping");
        Dispatcher = _app.CreateHttpClient("dispatcher");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    // Helpers shared by the tests.

    public async Task<Guid> PlaceOrderAsync(string customer)
    {
        var response = await Orders.PostAsJsonAsync("/api/orders", new { customer, product = "Keyboard", quantity = 1 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!.Id;
    }

    public async Task<OutboxDto> OutboxMessageForAsync(Guid orderId) =>
        await EventuallyAsync(async () =>
            (await Orders.GetFromJsonAsync<OutboxDto[]>("/api/outbox"))!
                .FirstOrDefault(m => m.Payload.Contains(orderId.ToString()) && m.ProcessedOnUtc is not null));

    public async Task<DeliveryDto[]> DeliveriesForAsync(string messageId) =>
        (await Dispatcher.GetFromJsonAsync<DeliveryDto[]>("/api/deliveries"))!
            .Where(d => d.MessageId == messageId)
            .OrderBy(d => d.AtUtc)
            .ToArray();

    public async Task<ShipmentDto[]> ShipmentsForAsync(Guid orderId) =>
        (await Shipping.GetFromJsonAsync<ShipmentDto[]>("/api/shipments"))!.Where(s => s.OrderId == orderId).ToArray();

    // Polls until the function returns something (the flow is asynchronous by design).
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await probe();
            if (value is not null) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(250);
        }
    }

    public sealed record OrderDto(Guid Id);
    public sealed record OutboxDto(Guid Id, string Payload, DateTimeOffset? ProcessedOnUtc);
    public sealed record DeliveryDto(string MessageId, int DeliveryCount, string Outcome, string? Detail, DateTimeOffset AtUtc);
    public sealed record ShipmentDto(Guid Id, Guid OrderId, string CreatedVia);
}
