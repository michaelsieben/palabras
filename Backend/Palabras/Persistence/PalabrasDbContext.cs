using Microsoft.EntityFrameworkCore;

namespace Palabras.Persistence;

internal sealed class PalabrasDbContext : DbContext
{
    public PalabrasDbContext(DbContextOptions<PalabrasDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_role", "role IN ('owner', 'reviewer')");
            table.HasCheckConstraint("ck_users_username", "length(username) BETWEEN 3 AND 32 AND username = lower(trim(username)) COLLATE BINARY AND username NOT GLOB '*[^a-z0-9._-]*'");
        });
        user.HasKey(value => value.Id);
        user.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        user.Property(value => value.Username).HasColumnName("username").UseCollation("NOCASE").IsRequired();
        user.HasIndex(value => value.Username).IsUnique();
        user.Property(value => value.Role).HasColumnName("role").IsRequired();
        // A unique role plus its CHECK permits exactly one owner and one reviewer, at most.
        user.HasIndex(value => value.Role).IsUnique();
        user.Property(value => value.PasswordHash).HasColumnName("password_hash").IsRequired();
        user.Property(value => value.CreatedAt).HasColumnName("created_at");
        user.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        user.Property(value => value.LastLoginAt).HasColumnName("last_login_at");
    }
}
