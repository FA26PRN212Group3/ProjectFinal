using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Models;

public class PaymentCheckout
{
    public int OrderId { get; set; }
    public string TxnRef { get; set; } = "";
    public string TmnCode { get; set; } = "";
    public decimal Amount { get; set; }
    public string CreateDate { get; set; } = "";
    public string ExpireDate { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTimeOffset? NextQueryAt { get; set; }
}

public class PaymentActivity
{
    public long Id { get; set; }
    public int? OrderId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EventKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Details { get; set; } = "";
}

public partial class ConcertDbContext
{
    public virtual DbSet<PaymentCheckout> PaymentCheckouts { get; set; }
    public virtual DbSet<PaymentActivity> PaymentActivities { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentCheckout>(entity =>
        {
            entity.ToTable("PaymentCheckout");
            entity.HasKey(e => e.OrderId);
            entity.HasOne<Order>().WithOne().HasForeignKey<PaymentCheckout>(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.TxnRef).IsUnique();
            entity.Property(e => e.TxnRef).HasMaxLength(100);
            entity.Property(e => e.TmnCode).HasMaxLength(8);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,0)");
            entity.Property(e => e.CreateDate).HasMaxLength(14);
            entity.Property(e => e.ExpireDate).HasMaxLength(14);
            entity.Property(e => e.ReturnUrl).HasMaxLength(255);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
        });
        modelBuilder.Entity<PaymentActivity>(entity =>
        {
            entity.ToTable("PaymentActivity");
            entity.HasKey(e => e.Id);
            entity.HasOne<Order>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.EventKey).IsUnique();
            entity.HasIndex(e => new { e.OrderId, e.OccurredAt, e.Id });
            entity.Property(e => e.EventKey).HasMaxLength(120);
            entity.Property(e => e.EventType).HasMaxLength(50);
            entity.Property(e => e.Outcome).HasMaxLength(10);
            entity.Property(e => e.Details).HasMaxLength(300);
        });
    }
}
