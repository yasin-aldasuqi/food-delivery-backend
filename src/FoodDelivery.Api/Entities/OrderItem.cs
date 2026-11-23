namespace FoodDelivery.Api.Entities;

public class OrderItem
{
    public int Id { get; set; }
    public int Quantity { get; set; }

    // Snapshot of the price at order time, so later menu price
    // changes don't rewrite the history of past orders.
    public decimal UnitPrice { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public int MenuItemId { get; set; }
    public MenuItem MenuItem { get; set; } = null!;
}
