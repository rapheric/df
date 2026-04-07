using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Helpers;
using NCBA.DCL.Models;

namespace NCBA.DCL.Data
{
    public static class DbInitializer
    {
        private static bool IsSafeSqlIdentifier(string identifier)
        {
            return !string.IsNullOrWhiteSpace(identifier)
                && identifier.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        }

        private static async Task EnsureColumnExistsAsync(
            ApplicationDbContext context,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            if (!IsSafeSqlIdentifier(tableName) || !IsSafeSqlIdentifier(columnName))
            {
                throw new InvalidOperationException("Unsafe SQL identifier detected during schema initialization.");
            }

            var exists = await context.Database.SqlQueryRaw<int>($@"
                SELECT COUNT(*) AS Value
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = {{0}}
                  AND COLUMN_NAME = {{1}}",
                tableName,
                columnName)
                .SingleAsync();

            if (exists > 0)
            {
                return;
            }

#pragma warning disable EF1002
            await context.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {columnDefinition}");
#pragma warning restore EF1002
        }

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
                    await EnsureColumnExistsAsync(context, "Deferrals", "NextDueDate", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Deferrals", "NextDocumentDueDate", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Deferrals", "SlaExpiry", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Extensions", "NextDueDate", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Extensions", "NextDocumentDueDate", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Extensions", "SlaExpiry", "datetime(6) NULL");
                    await EnsureColumnExistsAsync(context, "Deferrals", "DaysSought", "int NOT NULL DEFAULT 0");
                    await EnsureColumnExistsAsync(context, "DeferralDocuments", "DaysSought", "int NULL");
                    await EnsureColumnExistsAsync(context, "DeferralDocuments", "NextDocumentDueDate", "datetime(6) NULL");

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