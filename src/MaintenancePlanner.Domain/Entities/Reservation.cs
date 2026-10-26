namespace MaintenancePlanner.Domain.Entities;

public sealed class Reservation
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Draft? Draft { get; set; }
    public string SparePartId { get; set; } = string.Empty;
    public SparePart? SparePart { get; set; }
    public decimal QuantityReserved { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum ReservationStatus
{
    Pending,
    Confirmed,
    Cancelled
}
