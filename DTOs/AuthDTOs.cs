using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NCBA.DCL.Models;

namespace NCBA.DCL.DTOs;

public class RegisterAdminRequest
{
    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// MFA verification token (TOTP code or backup code)
    /// Only required if MFA is enabled
    /// </summary>
    [StringLength(20)]
    public string? MFAToken { get; set; }
}

public class LoginResponse
{
    public string? Token { get; set; }
    public UserResponse? User { get; set; }

    /// <summary>
    /// Session token for MFA verification (sent if MFA is required)
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("mfaSessionToken")]
    public string? MFASessionToken { get; set; }

    /// <summary>
    /// Is MFA verification required
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isMFARequired")]
    public bool IsMFARequired { get; set; } = false;

    /// <summary>
    /// MFA method being used (EMAIL, TOTP, etc.)
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("mfaMethod")]
    public string? MFAMethod { get; set; }

    /// <summary>
    /// (DEVELOPMENT ONLY) The actual MFA code for testing
    /// This should NEVER be sent in production
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("devTestCode")]
    public string? DevTestCode { get; set; }
}

public class UserResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsMFAEnabled { get; set; } = false;
}

public class CreateUserRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public UserRole Role { get; set; }

    public string? CustomerNumber { get; set; }
    public string? CustomerId { get; set; }
    public string? RmId { get; set; }
}

public class RegisterAuthDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginAuthDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Email MFA verification request
/// </summary>
public class VerifyEmailMFARequest
{
    [Required]
    [StringLength(255)]
    public string SessionToken { get; set; } = string.Empty;

    [Required]
    [StringLength(6)]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Request to resend MFA code
/// </summary>
public class ResendMFACodeRequest
{
    [Required]
    [StringLength(255)]
    public string SessionToken { get; set; } = string.Empty;
}

