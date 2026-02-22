namespace NCBA.DCL.Services;

public interface IEmailService
{
    Task SendDeferralSubmittedAsync(string toEmail, string userName, string deferralNumber, string customerName, int daysSought, string recipientRole);
    Task SendDeferralReminderAsync(string toEmail, string userName, string deferralNumber, string customerName);
    Task SendDeferralApprovalConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval);
    Task SendDeferralApprovedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval);
    Task SendDeferralReturnedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment, string returnedByName);
    Task SendDeferralReturnConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment);
    Task SendDeferralRejectedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason, string rejectedByName);
    Task SendDeferralRejectConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason);
    Task SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status);
    Task SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName);
    Task SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status);

    // ✅ NEW: Checker approval/return notifications (aligns with Node.js)
    Task SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);
    Task SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);
    Task SendFirstApproverReplacedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacementName);
    Task SendFirstApproverAssignedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacedName);

    // ✅ NEW: Email verification and logout verification
    Task SendEmailVerificationCodeAsync(string toEmail, string verificationCode, int expiryMinutes);
    Task SendLogoutVerificationEmailAsync(string toEmail, string userName, string verificationCode, string ipAddress, string userAgent);
}