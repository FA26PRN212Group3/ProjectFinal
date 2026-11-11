using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class Order
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int ZoneId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TotalAmount { get; set; }

    public string Status { get; set; } = null!;

    public string? GoogleEventId { get; set; }

    public string? GoogleEventLink { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public virtual Payment? Payment { get; set; }

    public virtual ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();

    public virtual User User { get; set; } = null!;

    public virtual Zone Zone { get; set; } = null!;
}
