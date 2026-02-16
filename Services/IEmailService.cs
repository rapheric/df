namespace NCBA.DCL.Services;

public interface IEmailService
{
    Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent);
    Task<bool> SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status);
    Task<bool> SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName);
    Task<bool> SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status);

    // ✅ NEW: Checker approval/return notifications (aligns with Node.js)
    Task<bool> SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);
    Task<bool> SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);

    // ✅ NEW: Authentication email notifications
    Task<bool> SendEmailVerificationCodeAsync(string toEmail, string code, int expiryMinutes = 15);
    Task<bool> SendLogoutVerificationEmailAsync(string toEmail, string userName, string code, string ipAddress, string userAgent);
    Task<bool> SendMFAEnabledNotificationAsync(string toEmail, string userName, int backupCodeCount = 10);
}

