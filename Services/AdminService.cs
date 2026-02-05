using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.DTOs;

namespace NCBA.DCL.Services
{
    public class AdminService : IAdminService
    {
        private readonly Data.ApplicationDbContext _db;
        public AdminService(Data.ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<(int StatusCode, object Body)> RegisterAdminAsync(RegisterAdminDto dto)
        {
            // Check if admin already exists
            var existingAdmin = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email && u.Role == Models.UserRole.Admin);
            if (existingAdmin != null)
            {
                return (400, new { message = "Admin already exists" });
            }

            // Hash password
            var hashedPassword = BCrypt.Net.BCrypt.HashPassword(dto.Password);

            var admin = new Models.User
            {
                Name = dto.Username,
                Email = dto.Email,
                Password = hashedPassword,
                Role = Models.UserRole.Admin,
                Active = true
            };

            _db.Users.Add(admin);
            await _db.SaveChangesAsync();

            return (201, new
            {
                message = "Admin registered successfully",
                admin = new { username = admin.Name, email = admin.Email }
            });
        }

        private readonly Helpers.JwtTokenGenerator _jwtTokenGenerator;
        private readonly IConfiguration _configuration;

        public AdminService(Data.ApplicationDbContext db, Helpers.JwtTokenGenerator jwtTokenGenerator, IConfiguration configuration)
        {
            _db = db;
            _jwtTokenGenerator = jwtTokenGenerator;
            _configuration = configuration;
        }

        public async Task<(int StatusCode, object Body)> LoginAdminAsync(LoginAdminDto dto)
        {
            var admin = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email && u.Role == Models.UserRole.Admin);
            if (admin == null)
            {
                return (404, new { message = "Admin not found" });
            }

            var isMatch = Helpers.PasswordHasher.VerifyPassword(dto.Password, admin.Password);
            if (!isMatch)
            {
                return (400, new { message = "Invalid credentials" });
            }

            var token = _jwtTokenGenerator.GenerateToken(admin);

            return (200, new
            {
                message = "Login successful",
                token,
                admin = new { username = admin.Name, email = admin.Email }
            });
        }

        public async Task<(int StatusCode, object Body)> CreateUserAsync(CreateUserDto dto)
        {
            // Check if user already exists
            var exists = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
            if (exists != null)
            {
                return (400, new { message = "User already exists" });
            }

            string? rmId = null;
            string? customerNumber = null;

            if (dto.Role != null && dto.Role.ToLower() == "rm")
            {
                rmId = Guid.NewGuid().ToString();
            }

            if (dto.Role != null && dto.Role.ToLower() == "customer")
            {
                bool isUnique = false;
                var rand = new Random();
                while (!isUnique)
                {
                    var randomNumber = rand.Next(100000, 999999);
                    customerNumber = $"CUST-{randomNumber}";
                    var existingCustomer = await _db.Users.FirstOrDefaultAsync(u => u.CustomerNumber == customerNumber);
                    if (existingCustomer == null) isUnique = true;
                }
            }

            var hashedPassword = Helpers.PasswordHasher.HashPassword(dto.Password);

            var user = new Models.User
            {
                Name = dto.Name,
                Email = dto.Email,
                Password = hashedPassword,
                Role = Enum.TryParse<Models.UserRole>(dto.Role, true, out var role) ? role : Models.UserRole.Customer,
                RmId = rmId,
                CustomerNumber = customerNumber,
                Active = true
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return (201, user);
        }

        public async Task<(int StatusCode, object Body)> ToggleActiveAsync(string id)
        {
            if (!Guid.TryParse(id, out var userId))
                return (400, new { message = "Invalid user id" });
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return (404, new { message = "User not found" });
            user.Active = !user.Active;
            await _db.SaveChangesAsync();
            return (200, user);
        }

        public async Task<(int StatusCode, object Body)> ArchiveUserAsync(string id)
        {
            if (!Guid.TryParse(id, out var userId))
                return (400, new { message = "Invalid user id" });
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return (404, new { message = "User not found" });
            // Archive: set Active to false (C# model does not have isArchived, so use Active)
            user.Active = false;
            await _db.SaveChangesAsync();
            return (200, new { message = "User archived" });
        }

        public async Task<(int StatusCode, object Body)> TransferRoleAsync(string id, TransferRoleDto dto)
        {
            if (!Guid.TryParse(id, out var userId))
                return (400, new { message = "Invalid user id" });
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
                return (404, new { message = "User not found" });
            if (string.IsNullOrWhiteSpace(dto.NewRole) || !Enum.TryParse<Models.UserRole>(dto.NewRole, true, out var newRole))
                return (400, new { message = "Invalid new role" });
            user.Role = newRole;
            await _db.SaveChangesAsync();
            return (200, user);
        }

        public async Task<(int StatusCode, object Body)> ReassignTasksAsync(string id, ReassignTasksDto dto)
        {
            if (!Guid.TryParse(id, out var fromUserId))
                return (400, new { message = "Invalid source user id" });
            if (!Guid.TryParse(dto.NewAssigneeId, out var toUserId))
                return (400, new { message = "Invalid target user id" });
            var fromUser = await _db.Users.FindAsync(fromUserId);
            var toUser = await _db.Users.FindAsync(toUserId);
            if (fromUser == null)
                return (404, new { message = "Source user not found" });
            if (toUser == null)
                return (404, new { message = "Target user not found" });

            int deferrals = 0, extensions = 0, checklists = 0;

            // Deferral: update CreatedById
            var deferralList = _db.Deferrals.Where(d => d.CreatedById == fromUserId);
            foreach (var d in deferralList)
            {
                d.CreatedById = toUserId;
                deferrals++;
            }

            // Extension: update CreatedById
            var extensionList = _db.Extensions.Where(e => e.CreatedById == fromUserId);
            foreach (var e in extensionList)
            {
                e.CreatedById = toUserId;
                extensions++;
            }

            // Checklist: update CustomerId, AssignedToRMId, CreatedById, AssignedToCoCheckerId
            var checklistList = _db.Checklists.Where(c => c.CustomerId == fromUserId || c.AssignedToRMId == fromUserId || c.CreatedById == fromUserId || c.AssignedToCoCheckerId == fromUserId);
            foreach (var c in checklistList)
            {
                if (c.CustomerId == fromUserId) { c.CustomerId = toUserId; }
                if (c.AssignedToRMId == fromUserId) { c.AssignedToRMId = toUserId; }
                if (c.CreatedById == fromUserId) { c.CreatedById = toUserId; }
                if (c.AssignedToCoCheckerId == fromUserId) { c.AssignedToCoCheckerId = toUserId; }
                checklists++;
            }

            await _db.SaveChangesAsync();

            return (200, new
            {
                message = "Tasks reassigned successfully",
                fromUser = fromUser.Name,
                toUser = toUser.Name,
                reassignedCount = new { deferrals, extensions, checklists }
            });
        }
    }
}

