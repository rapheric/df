using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Models;

namespace NCBA.DCL.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Checklist> Checklists { get; set; }
    public DbSet<Document> Documents { get; set; }
    public DbSet<DocumentCategory> DocumentCategories { get; set; }
    public DbSet<CoCreatorFile> CoCreatorFiles { get; set; }
    public DbSet<ChecklistLog> ChecklistLogs { get; set; }
    public DbSet<Deferral> Deferrals { get; set; }
    public DbSet<Facility> Facilities { get; set; }
    public DbSet<DeferralDocument> DeferralDocuments { get; set; }
    public DbSet<Approver> Approvers { get; set; }
    public DbSet<UserLog> UserLogs { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<Upload> Uploads { get; set; }
    public DbSet<Extension> Extensions { get; set; }
    public DbSet<ExtensionApprover> ExtensionApprovers { get; set; }
    public DbSet<ExtensionHistory> ExtensionHistories { get; set; }
    public DbSet<ExtensionComment> ExtensionComments { get; set; }
    public DbSet<ExtensionFile> ExtensionFiles { get; set; }
    public DbSet<SupportingDoc> SupportingDocs { get; set; }

    // MFA and Security Models
    public DbSet<MFASetup> MFASetups { get; set; }
    public DbSet<MFALog> MFALogs { get; set; }
    public DbSet<TrustedDevice> TrustedDevices { get; set; }

    // SSO Models
    public DbSet<SSOProvider> SSOProviders { get; set; }
    public DbSet<SSOConnection> SSOConnections { get; set; }
    public DbSet<SSOLog> SSOLogs { get; set; }

    // Email and Logout Verification Models
    public DbSet<LogoutSession> LogoutSessions { get; set; }
    public DbSet<EmailVerification> EmailVerifications { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.CustomerId).IsUnique();
            entity.HasIndex(e => e.RmId).IsUnique();

            entity.Property(e => e.Role)
                .HasConversion<string>();

            // Navigation: User creates checklists
            entity.HasMany(u => u.CreatedChecklists)
                .WithOne(c => c.CreatedBy)
                .HasForeignKey(c => c.CreatedById)
                .OnDelete(DeleteBehavior.SetNull);

            // Navigation: User assigned as RM
            entity.HasMany(u => u.AssignedAsRM)
                .WithOne(c => c.AssignedToRM)
                .HasForeignKey(c => c.AssignedToRMId)
                .OnDelete(DeleteBehavior.SetNull);

            // Navigation: User assigned as CoChecker
            entity.HasMany(u => u.AssignedAsCoChecker)
                .WithOne(c => c.AssignedToCoChecker)
                .HasForeignKey(c => c.AssignedToCoCheckerId)
                .OnDelete(DeleteBehavior.SetNull);

            // UserLog relations
            entity.HasMany(u => u.TargetUserLogs)
                .WithOne(l => l.TargetUser)
                .HasForeignKey(l => l.TargetUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(u => u.PerformedByLogs)
                .WithOne(l => l.PerformedBy)
                .HasForeignKey(l => l.PerformedById)
                .OnDelete(DeleteBehavior.SetNull);

            // Deferrals
            entity.HasMany(u => u.CreatedDeferrals)
                .WithOne(d => d.CreatedBy)
                .HasForeignKey(d => d.CreatedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Checklist configuration
        modelBuilder.Entity<Checklist>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status)
                .HasConversion<string>();

            // Customer relationship (optional)
            entity.HasOne(c => c.Customer)
                .WithMany()
                .HasForeignKey(c => c.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // DocumentCategory configuration
        modelBuilder.Entity<DocumentCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(dc => dc.Checklist)
                .WithMany(c => c.Documents)
                .HasForeignKey(dc => dc.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Document configuration
        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status)
                .HasConversion<string>();
            entity.Property(e => e.CreatorStatus)
                .HasConversion<string>();
            entity.Property(e => e.CheckerStatus)
                .HasConversion<string>();
            entity.Property(e => e.RmStatus)
                .HasConversion<string>();

            entity.HasOne(d => d.DocumentCategory)
                .WithMany(dc => dc.DocList)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CoCreatorFile configuration
        modelBuilder.Entity<CoCreatorFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(f => f.Document)
                .WithMany(d => d.CoCreatorFiles)
                .HasForeignKey(f => f.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ChecklistLog configuration
        modelBuilder.Entity<ChecklistLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(l => l.Checklist)
                .WithMany(c => c.Logs)
                .HasForeignKey(l => l.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(l => l.User)
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Deferral configuration
        modelBuilder.Entity<Deferral>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeferralNumber).IsUnique();
            entity.Property(e => e.Status)
                .HasConversion<string>();
        });

        // Facility configuration
        modelBuilder.Entity<Facility>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Sanctioned)
                .HasColumnType("decimal(18,2)");
            entity.Property(e => e.Balance)
                .HasColumnType("decimal(18,2)");
            entity.Property(e => e.Headroom)
                .HasColumnType("decimal(18,2)");

            entity.HasOne(f => f.Deferral)
                .WithMany(d => d.Facilities)
                .HasForeignKey(f => f.DeferralId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DeferralDocument configuration
        modelBuilder.Entity<DeferralDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(d => d.Deferral)
                .WithMany(def => def.Documents)
                .HasForeignKey(d => d.DeferralId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.UploadedBy)
                .WithMany()
                .HasForeignKey(d => d.UploadedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Approver configuration
        modelBuilder.Entity<Approver>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(a => a.Deferral)
                .WithMany(d => d.Approvers)
                .HasForeignKey(a => a.DeferralId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UserLog configuration
        modelBuilder.Entity<UserLog>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        // Notification configuration
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(n => n.User)
                .WithMany(u => u.Notifications)
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Upload configuration
        modelBuilder.Entity<Upload>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileSize).HasColumnType("bigint");
            entity.HasIndex(e => e.ChecklistId);
            entity.HasIndex(e => e.DocumentId);
            entity.Property(e => e.Status).HasMaxLength(50);
        });

        // Extension configuration
        modelBuilder.Entity<Extension>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.CreatorApprovalStatus).HasConversion<string>();
            entity.Property(e => e.CheckerApprovalStatus).HasConversion<string>();

            entity.HasOne(e => e.Deferral).WithMany().HasForeignKey(e => e.DeferralId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.RequestedBy).WithMany().HasForeignKey(e => e.RequestedById).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CreatorApprovedBy).WithMany().HasForeignKey(e => e.CreatorApprovedById).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.CheckerApprovedBy).WithMany().HasForeignKey(e => e.CheckerApprovedById).OnDelete(DeleteBehavior.SetNull);
        });

        // ExtensionApprover configuration
        modelBuilder.Entity<ExtensionApprover>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ApprovalStatus).HasConversion<string>();
            entity.HasOne(ea => ea.Extension).WithMany(e => e.Approvers).HasForeignKey(ea => ea.ExtensionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(ea => ea.User).WithMany().HasForeignKey(ea => ea.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        // ExtensionHistory configuration
        modelBuilder.Entity<ExtensionHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(eh => eh.Extension).WithMany(e => e.History).HasForeignKey(eh => eh.ExtensionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(eh => eh.User).WithMany().HasForeignKey(eh => eh.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        // ExtensionComment configuration
        modelBuilder.Entity<ExtensionComment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(ec => ec.Extension).WithMany(e => e.Comments).HasForeignKey(ec => ec.ExtensionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(ec => ec.Author).WithMany().HasForeignKey(ec => ec.AuthorId).OnDelete(DeleteBehavior.SetNull);
        });

        // ExtensionFile configuration
        modelBuilder.Entity<ExtensionFile>(entity =>
        {
            entity.HasKey(ef => ef.Id);
            entity.HasOne(ef => ef.Extension).WithMany(e => e.AdditionalFiles).HasForeignKey(ef => ef.ExtensionId).OnDelete(DeleteBehavior.Cascade);
        });

        // SupportingDoc configuration
        modelBuilder.Entity<SupportingDoc>(entity =>
        {
            entity.HasKey(sd => sd.Id);
            entity.HasOne(sd => sd.Checklist).WithMany(c => c.SupportingDocs).HasForeignKey(sd => sd.ChecklistId).OnDelete(DeleteBehavior.Cascade);
        });

        // MFA Setup configuration
        modelBuilder.Entity<MFASetup>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasOne(m => m.User)
                .WithOne(u => u.MFASetup)
                .HasForeignKey<MFASetup>(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => m.UserId).IsUnique();
        });

        // MFA Log configuration
        modelBuilder.Entity<MFALog>(entity =>
        {
            entity.HasKey(ml => ml.Id);
            entity.HasOne(ml => ml.User)
                .WithMany(u => u.MFALogs)
                .HasForeignKey(ml => ml.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(ml => new { ml.UserId, ml.CreatedAt });
        });

        // Trusted Device configuration
        modelBuilder.Entity<TrustedDevice>(entity =>
        {
            entity.HasKey(td => td.Id);
            entity.HasOne(td => td.User)
                .WithMany(u => u.TrustedDevices)
                .HasForeignKey(td => td.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(td => new { td.UserId, td.DeviceFingerprint });
        });

        // SSO Provider configuration
        modelBuilder.Entity<SSOProvider>(entity =>
        {
            entity.HasKey(sp => sp.Id);
            entity.HasIndex(sp => sp.ProviderName).IsUnique();
            entity.HasMany(sp => sp.UserConnections)
                .WithOne(sc => sc.Provider)
                .HasForeignKey(sc => sc.SSOProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(sp => sp.SSOLogs)
                .WithOne(sl => sl.Provider)
                .HasForeignKey(sl => sl.SSOProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SSO Connection configuration
        modelBuilder.Entity<SSOConnection>(entity =>
        {
            entity.HasKey(sc => sc.Id);
            entity.HasOne(sc => sc.User)
                .WithMany(u => u.SSOConnections)
                .HasForeignKey(sc => sc.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(sc => sc.Provider)
                .WithMany(p => p.UserConnections)
                .HasForeignKey(sc => sc.SSOProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(sc => new { sc.UserId, sc.SSOProviderId }).IsUnique();
            entity.HasIndex(sc => new { sc.SSOProviderId, sc.ProviderUserId }).IsUnique();
        });

        // SSO Log configuration
        modelBuilder.Entity<SSOLog>(entity =>
        {
            entity.HasKey(sl => sl.Id);
            entity.HasOne(sl => sl.User)
                .WithMany(u => u.SSOLogs)
                .HasForeignKey(sl => sl.UserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(sl => sl.Provider)
                .WithMany(p => p.SSOLogs)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(sl => new { sl.UserId, sl.CreatedAt });
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is User || e.Entity is Checklist ||
                       e.Entity is Document || e.Entity is Deferral ||
                       e.Entity is Notification);

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Modified)
            {
                if (entry.Entity.GetType().GetProperty("UpdatedAt") != null)
                {
                    entry.Property("UpdatedAt").CurrentValue = DateTime.UtcNow;
                }
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

