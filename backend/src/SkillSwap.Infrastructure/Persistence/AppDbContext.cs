using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Domain.Common;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence;

// Роли хранятся колонкой Users.Role (раздел 13 ТЗ), поэтому таблицы ролей Identity не нужны.
public class AppDbContext : IdentityUserContext<User, Guid>, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>Ключи Data Protection (раздел 10.3 ТЗ): ими защищены токены Identity, позже — и чаты.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<UserLanguage> UserLanguages => Set<UserLanguage>();
    public DbSet<UserSkill> UserSkills => Set<UserSkill>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<SkillSuggestion> SkillSuggestions => Set<SkillSuggestion>();
    public DbSet<SkillCategory> SkillCategories => Set<SkillCategory>();
    public DbSet<Skill> Skills => Set<Skill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyTimestamps();
        return base.SaveChanges();
    }

    // Все даты в UTC (CLAUDE.md): CreatedAt и UpdatedAt проставляются здесь, а не в сервисах.
    private void ApplyTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamped>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
