using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Helpers;
using NCBA.DCL.Models;

namespace NCBA.DCL.Data
{
    public static class DbInitializer
    {
        public static async Task SeedData(ApplicationDbContext context)
        {
            try
            {
                // Get pending migrations
                var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

                // Only apply migrations if there are pending ones
                if (pendingMigrations.Any())
                {
                    try
                    {
                        Console.WriteLine($"⏳ Applying {pendingMigrations.Count()} pending migrations...");
                        await context.Database.MigrateAsync();
                        Console.WriteLine("✅ Database migrations applied successfully");
                    }
                    catch (Exception migrationEx)
                    {
                        // Log the migration error but continue
                        Console.WriteLine($"⚠️  Migration warning: {migrationEx.InnerException?.Message ?? migrationEx.Message}");
                        Console.WriteLine("📌 Continuing with database initialization despite migration issues...");
                    }
                }

                // Ensure missing columns exist (as a fallback for migration issues)
                try
                {
                    // Add NextDueDate to Deferrals
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Deferrals ADD COLUMN NextDueDate datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    // Add NextDocumentDueDate to Deferrals
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Deferrals ADD COLUMN NextDocumentDueDate datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    // Add SlaExpiry to Deferrals
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Deferrals ADD COLUMN SlaExpiry datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    // Add date columns to Extensions
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Extensions ADD COLUMN NextDueDate datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Extensions ADD COLUMN NextDocumentDueDate datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Extensions ADD COLUMN SlaExpiry datetime(6) NULL");
                    }
                    catch { /* Column already exists */ }

                    Console.WriteLine("✅ Database schema columns verified");
                }
                catch (Exception schemaEx)
                {
                    Console.WriteLine($"⚠️  Schema check error (non-critical): {schemaEx.Message}");
                }

                // Seed admin user if it doesn't exist
                if (!await context.Users.AnyAsync(u => u.Email == "admin@ncba.com"))
                {
                    var admin = new User
                    {
                        Id = Guid.NewGuid(),
                        Name = "Super Admin",
                        Email = "admin@ncba.com",
                        Password = PasswordHasher.HashPassword("Password@123"),
                        Role = UserRole.Admin,
                        Active = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    context.Users.Add(admin);
                    await context.SaveChangesAsync();
                    Console.WriteLine("✅ Admin user seeded: admin@ncba.com / Password@123");
                }
                else
                {
                    Console.WriteLine("ℹ️  Admin user already exists");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Unexpected error initializing database: {ex.Message}");
                // Don't throw - let the app continue even if seeding fails
            }
        }
    }
}
