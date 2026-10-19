using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ShortLink> ShortLinks => Set<ShortLink>();
    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite returns DateTimeKind.Unspecified; all stored values are UTC by domain contract.
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        var keys = modelBuilder.Entity<IdempotencyRecord>();
        keys.ToTable("IdempotencyRecords");
        keys.HasKey(x => x.Key);
        keys.Property(x => x.Key).HasMaxLength(128);
        keys.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        keys.HasOne(x => x.Link).WithMany().HasForeignKey(x => x.ShortLinkId).OnDelete(DeleteBehavior.Restrict);

        var links = modelBuilder.Entity<ShortLink>();
        links.ToTable("ShortLinks", table => table.HasCheckConstraint("CK_ShortLinks_ClickCount", "ClickCount >= 0"));
        links.HasKey(link => link.Id);
        links.Property(link => link.Code).HasMaxLength(30).IsRequired();
        links.HasIndex(link => link.Code).IsUnique();
        links.Property(link => link.LongUrl).HasMaxLength(2048).IsRequired();
        links.Property(link => link.CreatedAtUtc).HasConversion(utc);
        links.Property(link => link.ExpiresAtUtc).HasConversion(utc);

        var clicks = modelBuilder.Entity<ClickEvent>();
        clicks.ToTable("ClickEvents");
        clicks.HasKey(click => click.Id);
        clicks.Property(click => click.ClickedAtUtc).HasConversion(utc);
        clicks.Property(click => click.IpHash).HasMaxLength(64);
        clicks.HasIndex(click => new { click.ShortLinkId, click.ClickedAtUtc });
        clicks.HasOne<ShortLink>().WithMany().HasForeignKey(click => click.ShortLinkId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
