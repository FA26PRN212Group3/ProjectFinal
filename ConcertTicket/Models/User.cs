using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class User
{
    public int Id { get; set; }

    public string GoogleSub { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}
