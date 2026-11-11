using System;
using System.Collections.Generic;

namespace ConcertTicket.Models;

public partial class Payment
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Provider { get; set; } = null!;

    public decimal Amount { get; set; }

    public string? TransactionNo { get; set; }

    public string? BankCode { get; set; }

    public string ResponseCode { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Order Order { get; set; } = null!;
}
