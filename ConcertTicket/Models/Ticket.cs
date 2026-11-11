using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class Ticket
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string TicketCode { get; set; } = null!;

    public string Gate { get; set; } = null!;

    public string? QrImageUrl { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Order Order { get; set; } = null!;
}
