using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.DTOs;
using NCBA.DCL.Helpers;
using NCBA.DCL.Models;
using NCBA.DCL.Services;
using System.Security.Claims;

namespace NCBA.DCL.Controllers;

[ApiController]
[Route("api/admin/auth")]
public class AuthController : ControllerBase
{
    private const int MaxPasswordAttempts = 3;
    private readonly ApplicationDbContext _context;
    private readonly JwtTokenGenerator _tokenGenerator;
    private readonly IMFAService _mfaService;
    private readonly ISSOService _ssoService;
    private readonly IEmailService _emailService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ApplicationDbContext context,
        JwtTokenGenerator tokenGenerator,
        IMFAService mfaService,
        ISSOService ssoService,
        IEmailService emailService,
        ILogger<AuthController> logger)
    {
        _context = context;
        _tokenGenerator = tokenGenerator;
        _mfaService = mfaService;
        _ssoService = ssoService;
        _emailService = emailService;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<IActionResult> RegisterAdmin([FromBody] RegisterAdminRequest request)
    {
        try
        {
            var exists = await _context.Users.AnyAsync(u => u.Email == request.Email);
            if (exists)
            {
                return BadRequest(new { message = "Admin already exists" });
            }

            var admin = new User
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Email = request.Email,
                Password = PasswordHasher.HashPassword(request.Password),
                Role = UserRole.Admin,
                Active = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(admin);
            await _context.SaveChangesAsync();

            return StatusCode(201, new
            {
                message = "Admin registered successfully",
                user = new UserResponse
                {
                    Id = admin.Id,
                    Name = admin.Name,
                    Email = admin.Email,
                    Role = admin.Role.ToString(),
                    IsMFAEnabled = false
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering admin");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.MFASetup)
                .FirstOrDefaultAsync(u => u.Email == request.Email);

            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            if (!user.Active)
            {
                return StatusCode(403, new { message = "Account deactivated" });
            }

            if (user.IsPasswordLocked)
            {
                return StatusCode(423, new
                {
                    message = "Your password has been blocked after 3 failed attempts. Contact an administrator to unlock your account.",
                    isPasswordLocked = true
                });
            }

            if (!PasswordHasher.VerifyPassword(request.Password, user.Password))
            {
                user.FailedLoginAttempts += 1;
                user.UpdatedAt = DateTime.UtcNow;

                var remainingAttempts = Math.Max(0, MaxPasswordAttempts - user.FailedLoginAttempts);
                if (user.FailedLoginAttempts >= MaxPasswordAttempts)
                {
                    user.IsPasswordLocked = true;
                    user.PasswordLockedAt = DateTime.UtcNow;
                    await _mfaService.LogMFAAttemptAsync(user.Id, "PASSWORD", false, "Password locked after maximum failed attempts", GetClientIp());
                    await _context.SaveChangesAsync();

                    return StatusCode(423, new
                    {
                        message = "Your password has been blocked after 3 failed attempts. Contact an administrator to unlock your account.",
                        isPasswordLocked = true
                    });
                }

                await _mfaService.LogMFAAttemptAsync(user.Id, "PASSWORD", false, "Invalid password", GetClientIp());
                await _context.SaveChangesAsync();
                return Unauthorized(new
                {
                    message = $"Invalid credentials. {remainingAttempts} password attempt{(remainingAttempts == 1 ? string.Empty : "s")} remaining.",
                    remainingAttempts,
                    isPasswordLocked = false
                });
            }

            if (user.FailedLoginAttempts > 0 || user.IsPasswordLocked || user.PasswordLockedAt.HasValue)
            {
                user.FailedLoginAttempts = 0;
                user.IsPasswordLocked = false;
                user.PasswordLockedAt = null;
                user.UpdatedAt = DateTime.UtcNow;
            }

            // ✅ NEW: Send email-based MFA code on successful password verification
            // Generate 6-digit MFA code
            var mfaCode = GenerateSecureCode(6);
            var mfaCodeHash = HashCode(mfaCode);
            var mfaSessionToken = GenerateMFASessionToken(user.Id);

            // 🔐 LOG MFA CODE FOR TESTING (REMOVE IN PRODUCTION)
            Console.WriteLine($"🔒 [MFA CODE FOR TESTING] Code: {mfaCode} | Email: {user.Email} | SessionToken: {mfaSessionToken}");
            _logger.LogInformation($"🔒 [MFA CODE FOR TESTING] Code: {mfaCode} | Email: {user.Email}");

            // Add code to response header for development (visible in browser Network tab)
            Response.Headers.Append("X-MFA-Code-Dev", mfaCode);
            Response.Headers.Append("X-MFA-SessionToken-Dev", mfaSessionToken);

            // Store the MFA code (expires in 10 minutes)
            var emailMFARecord = new EmailMFACode
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Code = mfaCodeHash,
                SessionToken = mfaSessionToken,
                IsUsed = false,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                IpAddress = GetClientIp(),
                UserAgent = Request.Headers["User-Agent"].ToString()
            };

            _context.EmailMFACodes.Add(emailMFARecord);
            await _context.SaveChangesAsync();

            // ✅ Send MFA code via email to all users
            try
            {
                await _emailService.SendEmailVerificationCodeAsync(
                    user.Email,
                    mfaCode,
                    expiryMinutes: 10
                );
                _logger.LogInformation($"📧 MFA code sent to email: {user.Email}");
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx, $"Failed to send MFA code to {user.Email}");
                // Don't fail login if email fails, but log it
            }

            // ✅ Also prepare SMS code for phone number (0719266515 or user's phone)
            try
            {
                const string BACKUP_PHONE = "0719266515";
                // TODO: Integrate with SMS provider (Twilio, AWS SNS, etc.)
                // For now, log that SMS would be sent
                _logger.LogInformation($"📱 MFA code would be sent via SMS to phone: {BACKUP_PHONE}");

                // When SMS service is integrated, uncomment:
                // await _smsService.SendMFACodeAsync(BACKUP_PHONE, mfaCode);
            }
            catch (Exception smsEx)
            {
                _logger.LogError(smsEx, "Failed to send SMS MFA code");
                // Don't fail if SMS fails
            }

            // Return MFA required response
            return Ok(new LoginResponse
            {
                IsMFARequired = true,
                MFASessionToken = mfaSessionToken,
                MFAMethod = "EMAIL",
                User = null,
                Token = null,
                DevTestCode = mfaCode  // ⚠️ DEVELOPMENT ONLY - Remove in production!
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Verify email MFA code during login
    /// </summary>
    [HttpPost("verify-email-mfa")]
    public async Task<IActionResult> VerifyEmailMFA([FromBody] VerifyEmailMFARequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SessionToken) || string.IsNullOrWhiteSpace(request.Code))
            {
                return BadRequest(new { message = "Session token and code are required" });
            }

            // Find the MFA record
            var mfaRecord = await _context.EmailMFACodes
                .Include(e => e.User)
                .FirstOrDefaultAsync(e =>
                    e.SessionToken == request.SessionToken &&
                    !e.IsUsed &&
                    e.ExpiresAt > DateTime.UtcNow);

            if (mfaRecord == null)
            {
                _logger.LogWarning($"Invalid or expired MFA session: {request.SessionToken}");
                return BadRequest(new { message = "Invalid or expired MFA session" });
            }

            // Verify the code
            var isValidCode = VerifyCode(request.Code, mfaRecord.Code);
            if (!isValidCode)
            {
                _logger.LogWarning($"Invalid MFA code attempt for user {mfaRecord.User?.Email}");
                await _mfaService.LogMFAAttemptAsync(mfaRecord.UserId, "EMAIL_MFA", false, "Invalid code", GetClientIp());
                return Unauthorized(new { message = "Invalid MFA code" });
            }

            // Mark code as used
            mfaRecord.IsUsed = true;
            mfaRecord.VerifiedAt = DateTime.UtcNow;

            var user = mfaRecord.User;
            if (user == null)
            {
                return BadRequest(new { message = "User not found for this MFA session" });
            }

            // Generate JWT token
            var token = _tokenGenerator.GenerateToken(user);

            user.IsOnline = true;
            user.LoginTime = DateTime.UtcNow;
            user.LastSeen = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            // Log successful login
            var log = new UserLog
            {
                Id = Guid.NewGuid(),
                Action = "LOGIN",
                TargetUserId = user.Id,
                TargetEmail = user.Email,
                PerformedById = user.Id,
                PerformedByEmail = user.Email,
                Timestamp = DateTime.UtcNow
            };
            _context.UserLogs.Add(log);

            await _mfaService.LogMFAAttemptAsync(user.Id, "EMAIL_MFA", true, null, GetClientIp());
            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ User {user.Email} successfully verified email MFA");

            return Ok(new LoginResponse
            {
                Token = token,
                IsMFARequired = false,
                MFAMethod = "EMAIL",
                User = new UserResponse
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role.ToString(),
                    IsMFAEnabled = user.IsMFAEnabled
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying email MFA");
            return StatusCode(500, new { message = "Error verifying MFA code" });
        }
    }

    /// <summary>
    /// Resend MFA code via email
    /// </summary>
    [HttpPost("resend-mfa-code")]
    public async Task<IActionResult> ResendMFACode([FromBody] ResendMFACodeRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SessionToken))
            {
                return BadRequest(new { message = "Session token is required" });
            }

            // Find the MFA record
            var mfaRecord = await _context.EmailMFACodes
                .Include(e => e.User)
                .FirstOrDefaultAsync(e =>
                    e.SessionToken == request.SessionToken &&
                    !e.IsUsed &&
                    e.ExpiresAt > DateTime.UtcNow);

            if (mfaRecord == null)
            {
                return BadRequest(new { message = "Invalid or expired MFA session" });
            }

            // Generate new code
            var newCode = GenerateSecureCode(6);
            mfaRecord.Code = HashCode(newCode);
            mfaRecord.CreatedAt = DateTime.UtcNow;
            mfaRecord.ExpiresAt = DateTime.UtcNow.AddMinutes(5);

            // ✅ Send new MFA code via email to all users
            try
            {
                await _emailService.SendEmailVerificationCodeAsync(
                    mfaRecord.User!.Email,
                    newCode,
                    expiryMinutes: 5
                );
                _logger.LogInformation($"📧 MFA code resent to {mfaRecord.User.Email}");
            }
            catch (Exception emailEx)
            {
                _logger.LogError(emailEx, $"Failed to resend MFA code to {mfaRecord.User?.Email}");
            }

            // ✅ Also prepare SMS code resend for phone number
            try
            {
                const string BACKUP_PHONE = "0719266515";
                _logger.LogInformation($"📱 MFA code would be resent via SMS to phone: {BACKUP_PHONE}");
                // When SMS service is integrated: await _smsService.SendMFACodeAsync(BACKUP_PHONE, newCode);
            }
            catch (Exception smsEx)
            {
                _logger.LogError(smsEx, "Failed to resend SMS MFA code");
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "MFA code resent successfully",
                expiresIn = 600 // 10 minutes in seconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending MFA code");
            return StatusCode(500, new { message = "Error resending MFA code" });
        }
    }

    // ==================== MFA Endpoints ====================

    [Authorize]
    [HttpPost("mfa/setup")]
    public async Task<IActionResult> SetupMFA([FromBody] SetupMFARequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                return NotFound(new { message = "User not found" });

            // Generate TOTP secret
            var (secret, qrCodeUrl) = await _mfaService.GenerateTotpSecretAsync(user);

            // Generate session token for verification
            var sessionToken = GenerateMFASessionToken(userId);

            return Ok(new SetupMFAResponse
            {
                QRCodeUrl = qrCodeUrl,
                Secret = secret,
                SessionToken = sessionToken,
                Instructions = "Use an authenticator app (Google Authenticator, Authy, Microsoft Authenticator) to scan the QR code or enter the secret manually."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up MFA");
            return StatusCode(500, new { message = "Error setting up MFA" });
        }
    }

    [Authorize]
    [HttpPost("mfa/verify-setup")]
    public async Task<IActionResult> VerifyMFASetup([FromBody] VerifyMFASetupRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();

            // Verify the TOTP code against the session token
            var (isValid, backupCodes) = await _mfaService.VerifyAndEnableMFAAsync(userId, request.TOTPCode, request.SessionToken);

            if (!isValid)
            {
                await _mfaService.LogMFAAttemptAsync(userId, "SETUP", false, "Invalid TOTP code", GetClientIp());
                return BadRequest(new { message = "Invalid TOTP code. Please try again." });
            }

            await _mfaService.LogMFAAttemptAsync(userId, "SETUP", true, null, GetClientIp());

            return Ok(new VerifyMFAResponse
            {
                IsVerified = true,
                BackupCodes = backupCodes,
                Message = "MFA has been successfully enabled. Save your backup codes in a secure location."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying MFA setup");
            return StatusCode(500, new { message = "Error verifying MFA setup" });
        }
    }

    [Authorize]
    [HttpPost("mfa/disable")]
    public async Task<IActionResult> DisableMFA([FromBody] DisableMFARequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                return NotFound(new { message = "User not found" });

            // Verify password before disabling MFA
            if (!PasswordHasher.VerifyPassword(request.Password, user.Password))
            {
                return Unauthorized(new { message = "Invalid password" });
            }

            var success = await _mfaService.DisableMFAAsync(userId);

            if (!success)
                return BadRequest(new { message = "Failed to disable MFA" });

            return Ok(new { message = "MFA has been disabled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disabling MFA");
            return StatusCode(500, new { message = "Error disabling MFA" });
        }
    }

    [Authorize]
    [HttpPost("mfa/backup-codes")]
    public async Task<IActionResult> GenerateBackupCodes([FromBody] GenerateBackupCodesRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var codes = await _mfaService.GenerateBackupCodesAsync(userId);

            return Ok(new GenerateBackupCodesResponse { BackupCodes = codes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating backup codes");
            return StatusCode(500, new { message = "Error generating backup codes" });
        }
    }

    [Authorize]
    [HttpGet("mfa/status")]
    public async Task<IActionResult> GetMFAStatus()
    {
        try
        {
            var userId = GetCurrentUserId();
            var mfaSetup = await _context.MFASetups.FindAsync(userId);
            var trustedDevices = await _mfaService.GetTrustedDevicesAsync(userId);

            return Ok(new MFAStatusResponse
            {
                IsMFAEnabled = mfaSetup?.IsActive ?? false,
                IsTotpEnabled = mfaSetup?.IsTotpEnabled ?? false,
                IsBackupCodesEnabled = mfaSetup?.IsBackupCodesEnabled ?? false,
                EnabledAt = mfaSetup?.EnabledAt,
                LastTestedAt = mfaSetup?.LastTestedAt,
                TrustedDevices = trustedDevices.Select(td => new TrustedDeviceResponse
                {
                    Id = td.Id,
                    DeviceName = td.DeviceName ?? "Unknown Device",
                    DeviceType = td.DeviceType ?? "Unknown",
                    LastUsedAt = td.LastUsedAt,
                    IsActive = td.IsActive
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting MFA status");
            return StatusCode(500, new { message = "Error getting MFA status" });
        }
    }

    [Authorize]
    [HttpPost("mfa/trust-device")]
    public async Task<IActionResult> TrustDevice([FromBody] TrustedDeviceRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var device = await _mfaService.AddTrustedDeviceAsync(userId, request.DeviceFingerprint, request.DeviceName);

            return Ok(new TrustedDeviceResponse
            {
                Id = device.Id,
                DeviceName = device.DeviceName ?? "Unknown",
                DeviceType = device.DeviceType ?? "Unknown",
                LastUsedAt = device.LastUsedAt,
                IsActive = device.IsActive
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trusting device");
            return StatusCode(500, new { message = "Error trusting device" });
        }
    }

    // ==================== SSO Endpoints ====================

    [HttpGet("sso/providers")]
    public async Task<IActionResult> GetSSOProviders()
    {
        try
        {
            var providers = await _ssoService.GetEnabledProvidersAsync();

            return Ok(new SSOStatusResponse
            {
                AvailableProviders = providers.Select(p => new SSOProviderSetupDto
                {
                    Id = p.Id,
                    ProviderName = p.ProviderName,
                    DisplayName = p.DisplayName,
                    ProviderType = p.ProviderType,
                    IsEnabled = p.IsEnabled,
                    IconUrl = p.IconUrl,
                    DisplayOrder = p.DisplayOrder
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SSO providers");
            return StatusCode(500, new { message = "Error retrieving SSO providers" });
        }
    }

    [HttpPost("sso/authorize")]
    public async Task<IActionResult> InitializeSSOLogin([FromBody] SSOLoginInitializeRequest request)
    {
        try
        {
            var provider = await _context.SSOProviders.FindAsync(request.SSOProviderId);
            if (provider == null || !provider.IsEnabled)
                return NotFound(new { message = "SSO provider not found or disabled" });

            var authUrl = await _ssoService.GenerateAuthorizationUrlAsync(
                request.SSOProviderId,
                request.RedirectUri ?? $"{Request.Scheme}://{Request.Host}/auth/sso/callback",
                request.State
            );

            return Ok(new SSOLoginInitializeResponse { AuthorizationUrl = authUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing SSO login");
            return StatusCode(500, new { message = "Error with SSO initialization" });
        }
    }

    [HttpPost("sso/callback")]
    public async Task<IActionResult> HandleSSOCallback([FromBody] SSOCallbackRequest request)
    {
        try
        {
            var (success, user, isNewUser, message) = await _ssoService.HandleCallbackAsync(
                request.SSOProviderId,
                request.Code,
                request.State
            );

            if (!success)
                return BadRequest(new { message });

            if (user == null)
            {
                return BadRequest(new { message = "User not found" });
            }

            var token = _tokenGenerator.GenerateToken(user);

            user.IsOnline = true;
            user.LoginTime = DateTime.UtcNow;
            user.LastSeen = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new SSOCallbackResponse
            {
                Token = token,
                IsNewUser = isNewUser,
                User = new UserResponse
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role.ToString(),
                    IsMFAEnabled = user.IsMFAEnabled
                },
                Message = message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SSO callback");
            return StatusCode(500, new { message = "Error processing SSO login" });
        }
    }

    [Authorize]
    [HttpPost("sso/link")]
    public async Task<IActionResult> LinkSSOAccount([FromBody] LinkSSOAccountRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var provider = await _context.SSOProviders.FindAsync(request.SSOProviderId);

            if (provider == null)
                return NotFound(new { message = "SSO provider not found" });

            // Handle SSO callback to get user info, then link
            var (success, user, isNewUser, message) = await _ssoService.HandleCallbackAsync(
                request.SSOProviderId,
                request.Code
            );

            if (!success)
                return BadRequest(new { message });

            return Ok(new { message = "SSO account linked successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error linking SSO account");
            return StatusCode(500, new { message = "Error linking SSO account" });
        }
    }

    [Authorize]
    [HttpPost("sso/unlink")]
    public async Task<IActionResult> UnlinkSSOAccount([FromBody] UnlinkSSOAccountRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                return NotFound(new { message = "User not found" });

            // Verify password
            if (!PasswordHasher.VerifyPassword(request.Password, user.Password))
                return Unauthorized(new { message = "Invalid password" });

            var success = await _ssoService.UnlinkAccountAsync(userId, request.SSOProviderId);

            if (!success)
                return BadRequest(new { message = "Failed to unlink SSO account" });

            return Ok(new { message = "SSO account unlinked successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unlinking SSO account");
            return StatusCode(500, new { message = "Error unlinking SSO account" });
        }
    }

    [Authorize]
    [HttpGet("sso/connections")]
    public async Task<IActionResult> GetSSOConnections()
    {
        try
        {
            var userId = GetCurrentUserId();
            var connections = await _ssoService.GetUserConnectionsAsync(userId);

            return Ok(new SSOStatusResponse
            {
                LinkedAccounts = connections.Select(c => new SSOConnectionResponse
                {
                    Id = c.Id,
                    SSOProviderId = c.SSOProviderId,
                    ProviderName = c.Provider.ProviderName,
                    ProviderEmail = c.ProviderEmail ?? "",
                    ConnectedAt = c.CreatedAt
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SSO connections");
            return StatusCode(500, new { message = "Error retrieving SSO connections" });
        }
    }

    // ==================== Helper Methods ====================

    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("id");
        return Guid.Parse(userIdClaim?.Value ?? Guid.Empty.ToString());
    }

    private string GetClientIp()
    {
        return Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    private string GenerateMFASessionToken(Guid userId)
    {
        // Create a temporary session token that includes user ID and expiry
        var tokenString = $"{userId}:{DateTime.UtcNow.AddMinutes(15).Ticks}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tokenString));
    }

    // ==================== Enhanced Security Endpoints ====================

    /// <summary>
    /// Logout endpoint with email verification
    /// Sends verification code to user's email and logs the action
    /// </summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var userId = GetCurrentUserId();
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                return NotFound(new { message = "User not found" });

            user.IsOnline = false;
            user.LastSeen = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;

            // Immediate logout: record the action and return success.
            var userLog = new UserLog
            {
                Id = Guid.NewGuid(),
                Action = "LOGOUT",
                TargetUserId = userId,
                TargetEmail = user.Email,
                PerformedById = userId,
                PerformedByEmail = user.Email,
                Timestamp = DateTime.UtcNow
            };
            _context.UserLogs.Add(userLog);

            await _context.SaveChangesAsync();

            _logger.LogInformation($"User {user.Email} logged out");

            return Ok(new { message = "Logout successful" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            return StatusCode(500, new { message = "Error during logout" });
        }
    }

    /// <summary>
    /// Verify logout with email code
    /// Completes the logout process after email verification
    /// </summary>
    [HttpPost("verify-logout")]
    public async Task<IActionResult> VerifyLogout([FromBody] VerifyLogoutRequest request)
    {
        // Logout verification has been removed. Clients should call POST /api/admin/auth/logout
        return BadRequest(new { message = "Logout verification removed. Call POST /api/admin/auth/logout to logout immediately." });
    }

    /// <summary>
    /// Send email verification code
    /// Used during registration or email change
    /// </summary>
    [HttpPost("send-email-verification")]
    public async Task<IActionResult> SendEmailVerificationCode([FromBody] SendEmailVerificationRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Email))
                return BadRequest(new { message = "Email is required" });

            // Check if email is already verified
            var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
            if (existingUser?.EmailVerified == true)
                return BadRequest(new { message = "Email is already verified" });

            // Generate 6-digit verification code
            var verificationCode = GenerateSecureCode(6);
            var verificationCodeHash = HashCode(verificationCode);

            // Create email verification record
            var emailVerification = new EmailVerification
            {
                Id = Guid.NewGuid(),
                Email = request.Email,
                Code = verificationCodeHash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                IsUsed = false,
                CreatedAt = DateTime.UtcNow,
                UserId = existingUser?.Id
            };

            _context.EmailVerifications.Add(emailVerification);
            await _context.SaveChangesAsync();

            // Send verification email
            await _emailService.SendEmailVerificationCodeAsync(request.Email, verificationCode, expiryMinutes: 15);

            _logger.LogInformation($"Email verification code sent to {request.Email}");

            return Ok(new
            {
                message = "Verification code sent to your email",
                expiresIn = 900 // 15 minutes in seconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email verification");
            return StatusCode(500, new { message = "Error sending verification code" });
        }
    }

    /// <summary>
    /// Verify email with code
    /// Completes email verification process
    /// </summary>
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
                return BadRequest(new { message = "Email and code are required" });

            // Find the verification record
            var emailVerification = await _context.EmailVerifications
                .FirstOrDefaultAsync(ev =>
                    ev.Email == request.Email &&
                    !ev.IsUsed &&
                    ev.ExpiresAt > DateTime.UtcNow);

            if (emailVerification == null)
                return BadRequest(new { message = "No active verification found for this email" });

            // Verify the code
            var isValidCode = VerifyCode(request.Code, emailVerification.Code);
            if (!isValidCode)
            {
                _logger.LogWarning($"Invalid email verification code for {request.Email}");
                return BadRequest(new { message = "Incorrect verification code" });
            }

            // Mark email as verified
            var user = emailVerification.UserId.HasValue
                ? await _context.Users.FindAsync(emailVerification.UserId.Value)
                : await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);

            if (user != null)
            {
                user.EmailVerified = true;
                user.EmailVerifiedAt = DateTime.UtcNow;
            }

            // Mark verification as used
            emailVerification.IsUsed = true;
            emailVerification.VerifiedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation($"Email verified for {request.Email}");

            return Ok(new
            {
                message = "Email verified successfully",
                verifiedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying email");
            return StatusCode(500, new { message = "Error verifying email" });
        }
    }

    // ==================== Helper Methods ====================

    /// <summary>
    /// Generate a secure random code (e.g., 6-digit number)
    /// </summary>
    private string GenerateSecureCode(int length = 6)
    {
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            var tokenData = new byte[length];
            rng.GetBytes(tokenData);

            if (length == 6)
            {
                // Generate 6-digit code (000000-999999)
                var code = Math.Abs(BitConverter.ToInt32(tokenData, 0)) % 1000000;
                return code.ToString("D6");
            }

            // Fallback: Base64
            return Convert.ToBase64String(tokenData).Substring(0, length).Replace("/", "0").Replace("+", "1");
        }
    }

    /// <summary>
    /// Hash a code using SHA256 for secure storage
    /// </summary>
    private string HashCode(string code)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    /// <summary>
    /// Verify a code against its hash
    /// </summary>
    private bool VerifyCode(string code, string hash)
    {
        try
        {
            var codeHash = HashCode(code);
            return codeHash == hash;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// DTOs for logout and email verification
/// </summary>
public class LogoutSessionDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsVerified { get; set; }
}

public class VerifyLogoutRequest
{
    public Guid SessionId { get; set; }
    public string Code { get; set; } = string.Empty;
}

public class SendEmailVerificationRequest
{
    public string Email { get; set; } = string.Empty;
}

public class VerifyEmailRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
