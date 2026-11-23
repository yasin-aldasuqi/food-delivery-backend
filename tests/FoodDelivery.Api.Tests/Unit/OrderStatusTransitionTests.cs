using FoodDelivery.Api.Entities;

namespace FoodDelivery.Api.Tests.Unit;

public class OrderStatusTransitionTests
{
    private static Order OrderIn(OrderStatus status) =>
        new() { DeliveryAddress = "test", Status = status };

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Preparing)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Preparing, OrderStatus.OutForDelivery)]
    [InlineData(OrderStatus.OutForDelivery, OrderStatus.Delivered)]
    public void Allowed_transitions_are_accepted(OrderStatus from, OrderStatus to)
    {
        // Arrange
        var order = OrderIn(from);

        // Act
        var allowed = order.CanTransitionTo(to);

        // Assert
        Assert.True(allowed);
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Delivered)]       // skipping steps
    [InlineData(OrderStatus.Pending, OrderStatus.Preparing)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Cancelled)]     // too late to cancel
    [InlineData(OrderStatus.OutForDelivery, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Preparing, OrderStatus.Confirmed)]     // going backwards
    [InlineData(OrderStatus.Delivered, OrderStatus.Pending)]
    [InlineData(OrderStatus.Pending, OrderStatus.Pending)]         // no-op "change"
    public void Disallowed_transitions_are_rejected(OrderStatus from, OrderStatus to)
    {
        var order = OrderIn(from);

        Assert.False(order.CanTransitionTo(to));
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void Final_states_allow_no_transitions(OrderStatus finalState)
    {
        var order = OrderIn(finalState);

        foreach (var next in Enum.GetValues<OrderStatus>())
            Assert.False(order.CanTransitionTo(next), $"{finalState} -> {next} should be rejected");
    }
}
