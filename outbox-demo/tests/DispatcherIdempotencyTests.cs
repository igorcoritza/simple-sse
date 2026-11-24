using System.Net.Http.Json;

namespace OutboxDemo.Tests;

// Proves that the dispatcher (Service Bus -> Shipping API) can process the same message any
// number of times and Shipping still ends up with exactly one shipment.
public sealed class DispatcherIdempotencyTests(DemoAppFixture app) : IClassFixture<DemoAppFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Crash_after_Shipping_did_the_work_redelivers_but_creates_one_shipment()
    {
        // The dispatcher calls Shipping, Shipping commits the shipment, and then the dispatcher
        // "crashes" before completing the message. Service Bus delivers it again.
        await SetDispatcherChaosAsync(crashAfterDelivery: true);
        try
        {
            var orderId = await app.PlaceOrderAsync("crash-test");
            var message = await app.OutboxMessageForAsync(orderId);
            var messageId = message.Id.ToString();

            var deliveries = await DemoAppFixture.EventuallyAsync(async () =>
            {
                var d = await app.DeliveriesForAsync(messageId);
                return d.Any(x => x.Outcome == "already-processed") ? d : null;
            });

            // Delivery 1: Shipping created the shipment, the message was NOT completed.
            // Delivery 2: same Idempotency-Key, Shipping says "already processed", message completed.
            Assert.Equal(["simulated-crash", "already-processed"], deliveries.Select(d => d.Outcome));
            Assert.Equal([1, 2], deliveries.Select(d => d.DeliveryCount));

            var shipments = await app.ShipmentsForAsync(orderId);
            Assert.Single(shipments);
        }
        finally
        {
            await SetDispatcherChaosAsync(crashAfterDelivery: false);
        }
    }

    [Fact]
    public async Task Message_published_twice_creates_one_shipment()
    {
        // The relay publishes the same outbox row again (as if it crashed before marking it sent).
        var orderId = await app.PlaceOrderAsync("replay-test");
        var message = await app.OutboxMessageForAsync(orderId);
        var messageId = message.Id.ToString();
        await DemoAppFixture.EventuallyAsync(async () =>
            (await app.DeliveriesForAsync(messageId)).Any(d => d.Outcome == "created") ? true : (bool?)null);

        (await app.Orders.PostAsync($"/api/outbox/{message.Id}/replay", null, Ct)).EnsureSuccessStatusCode();

        // Second, independent copy of the message (new delivery, delivery count 1 again).
        var deliveries = await DemoAppFixture.EventuallyAsync(async () =>
        {
            var d = await app.DeliveriesForAsync(messageId);
            return d.Length >= 2 ? d : null;
        });
        Assert.Equal(["created", "already-processed"], deliveries.Select(d => d.Outcome));
        Assert.Single(await app.ShipmentsForAsync(orderId));
    }

    [Fact]
    public async Task Shipping_answers_the_same_shipment_for_the_same_idempotency_key()
    {
        // The contract the dispatcher relies on, tested directly against the Shipping API.
        var key = Guid.NewGuid().ToString();
        var order = new { orderId = Guid.NewGuid(), customer = "api-test", product = "Mouse", quantity = 1, placedOnUtc = DateTimeOffset.UtcNow };

        var first = await PostShipmentAsync(order, key);
        var second = await PostShipmentAsync(order, key);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.ShipmentId, second.ShipmentId);
        Assert.Single(await app.ShipmentsForAsync(order.orderId));
    }

    private async Task<ShipmentResult> PostShipmentAsync(object order, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shipments") { Content = JsonContent.Create(order) };
        request.Headers.Add("Idempotency-Key", key);
        var response = await app.Shipping.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShipmentResult>(Ct))!;
    }

    private async Task SetDispatcherChaosAsync(bool crashAfterDelivery) =>
        (await app.Dispatcher.PutAsJsonAsync("/api/chaos", new { crashAfterDelivery }, Ct)).EnsureSuccessStatusCode();

    private sealed record ShipmentResult(Guid ShipmentId, bool Duplicate);
}
