using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class Zone
{
    public int Id { get; set; }

    public int ConcertId { get; set; }

    public string Name { get; set; } = null!;

    public decimal Price { get; set; }

    public int TotalQuantity { get; set; }

    public int SoldQuantity { get; set; }

    public string Gate { get; set; } = null!;

    public virtual Concert Concert { get; set; } = null!;

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}
