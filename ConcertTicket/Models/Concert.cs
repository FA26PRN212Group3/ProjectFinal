using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class Concert
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string Artist { get; set; } = null!;

    public string Venue { get; set; } = null!;

    public string Address { get; set; } = null!;

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public string? PosterUrl { get; set; }

    public string? ZoneMapUrl { get; set; }

    public string? Description { get; set; }

    public virtual ICollection<Zone> Zones { get; set; } = new List<Zone>();
}
