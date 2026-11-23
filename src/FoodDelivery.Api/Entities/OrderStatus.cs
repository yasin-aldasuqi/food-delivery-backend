namespace FoodDelivery.Api.Entities;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Preparing,
    OutForDelivery,
    Delivered,
    Cancelled
}
