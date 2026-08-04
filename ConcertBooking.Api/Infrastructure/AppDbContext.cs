using ConcertBooking.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Concert> Concerts => Set<Concert>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingItem> BookingItems => Set<BookingItem>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherRedemption> VoucherRedemptions => Set<VoucherRedemption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TicketCategory>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_TicketCategory_AvailableQuantity", "\"AvailableQuantity\" >= 0");
                t.HasCheckConstraint("CK_TicketCategory_Price", "\"Price\" >= 0");
            });
            e.HasOne(c => c.Concert).WithMany(c => c.TicketCategories).HasForeignKey(c => c.ConcertId);
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.HasIndex(b => b.IdempotencyKey).IsUnique().HasDatabaseName("IX_Bookings_IdempotencyKey");
        });

        modelBuilder.Entity<BookingItem>(e =>
        {
            e.HasOne<Booking>().WithMany(b => b.Items).HasForeignKey(i => i.BookingId);
        });

        modelBuilder.Entity<Voucher>(e =>
        {
            e.HasIndex(v => v.Code).IsUnique();
        });

        modelBuilder.Entity<VoucherRedemption>(e =>
        {
            e.HasIndex(r => new { r.VoucherId, r.UserId }).IsUnique().HasDatabaseName("IX_VoucherRedemptions_VoucherId_UserId");
        });
    }
}
