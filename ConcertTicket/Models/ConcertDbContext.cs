using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Models;

public partial class ConcertDbContext : DbContext
{
    public ConcertDbContext(DbContextOptions<ConcertDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Concert> Concerts { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Ticket> Tickets { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<Zone> Zones { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Concert>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Concert__3214EC078A7F299D");

            entity.ToTable("Concert");

            entity.Property(e => e.Address).HasMaxLength(300);
            entity.Property(e => e.Artist).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.PosterUrl).HasMaxLength(500);
            entity.Property(e => e.Venue).HasMaxLength(200);
            entity.Property(e => e.ZoneMapUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Order__3214EC074AEEE92E");

            entity.ToTable("Order");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.GoogleEventId).HasMaxLength(200);
            entity.Property(e => e.GoogleEventLink).HasMaxLength(500);
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("Pending");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.User).WithMany(p => p.Orders)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Order_User");

            entity.HasOne(d => d.Zone).WithMany(p => p.Orders)
                .HasForeignKey(d => d.ZoneId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Order_Zone");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Payment__3214EC0775D86E58");

            entity.ToTable("Payment");

            entity.HasIndex(e => e.OrderId, "UQ__Payment__C3905BCEAA72E4BC").IsUnique();

            entity.Property(e => e.Amount).HasColumnType("decimal(18, 0)");
            entity.Property(e => e.BankCode).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.Provider)
                .HasMaxLength(20)
                .HasDefaultValue("VNPay");
            entity.Property(e => e.ResponseCode).HasMaxLength(10);
            entity.Property(e => e.TransactionNo).HasMaxLength(50);

            entity.HasOne(d => d.Order).WithOne(p => p.Payment)
                .HasForeignKey<Payment>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Payment_Order");
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Ticket__3214EC0756FF1337");

            entity.ToTable("Ticket");

            entity.HasIndex(e => e.TicketCode, "UQ__Ticket__598CF7A3B0939D3B").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.Gate).HasMaxLength(20);
            entity.Property(e => e.QrImageUrl).HasMaxLength(500);
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("Valid");
            entity.Property(e => e.TicketCode)
                .HasMaxLength(30)
                .IsUnicode(false);

            entity.HasOne(d => d.Order).WithMany(p => p.Tickets)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Ticket_Order");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__User__3214EC07FBB425CC");

            entity.ToTable("User");

            entity.HasIndex(e => e.GoogleSub, "UQ__User__41B3D6754AA157A4").IsUnique();

            entity.HasIndex(e => e.Email, "UQ__User__A9D10534824684FA").IsUnique();

            entity.Property(e => e.AvatarUrl).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysdatetime())");
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.FullName).HasMaxLength(100);
            entity.Property(e => e.GoogleSub).HasMaxLength(100);
        });

        modelBuilder.Entity<Zone>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Zone__3214EC074613B211");

            entity.ToTable("Zone");

            entity.HasIndex(e => new { e.ConcertId, e.Name }, "UQ_Zone_Name").IsUnique();

            entity.Property(e => e.Gate).HasMaxLength(20);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Price).HasColumnType("decimal(18, 0)");

            entity.HasOne(d => d.Concert).WithMany(p => p.Zones)
                .HasForeignKey(d => d.ConcertId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Zone_Concert");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
