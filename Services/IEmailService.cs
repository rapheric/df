namespace NCBA.DCL.Services;

public interface IEmailService
{
    Task<bool> SendDeferralSubmittedAsync(string toEmail, string userName, string deferralNumber, string customerName, int daysSought, string recipientRole);
    Task<bool> SendDeferralReminderAsync(string toEmail, string userName, string deferralNumber, string customerName);
    Task<bool> SendDeferralApprovalConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval);
    Task<bool> SendDeferralApprovedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string? nextApproverName, bool isFinalApproval);
    Task<bool> SendDeferralReturnedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment, string returnedByName);
    Task<bool> SendDeferralReturnConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string reworkComment);
    Task<bool> SendDeferralRejectedToRmAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason, string rejectedByName);
    Task<bool> SendDeferralRejectConfirmationAsync(string toEmail, string userName, string deferralNumber, string customerName, string rejectionReason);
    Task<bool> SendDeferralClosedAsync(string toEmail, string userName, string deferralNumber, string customerName, string closedByName, string closeComment);
    Task<bool> SendCheckerStatusChangedAsync(string toEmail, string userName, string dclNo, string status);
    Task<bool> SendDclSubmittedToRmAsync(string toEmail, string userName, string dclNo, string submittedByName);
    Task<bool> SendDclSubmittedToCoCheckerAsync(string toEmail, string userName, string dclNo, string submittedByName);
    Task<bool> SendDclReturnedToCoCreatorAsync(string toEmail, string userName, string dclNo, string submittedByName);
    Task<bool> SendExtensionApprovalRequestAsync(string toEmail, string userName, string deferralNumber, string requesterName);
    Task<bool> SendExtensionStatusUpdateAsync(string toEmail, string userName, string deferralNumber, string status);

    // ✅ NEW: Checker approval/return notifications (aligns with Node.js)
    Task<bool> SendCheckerApprovedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);
    Task<bool> SendCheckerReturnedAsync(string toEmail, string userName, string checklistId, string dclNo, string checkerName);
    Task<bool> SendFirstApproverReplacedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacementName);
    Task<bool> SendFirstApproverAssignedAsync(string toEmail, string userName, string deferralNumber, string customerName, string replacedName);
    Task<bool> SendApprovalFlowRemovedAsync(string toEmail, string userName, string deferralNumber, string customerName, string? currentApproverName);
    Task<bool> SendApprovalFlowAddedAsync(string toEmail, string userName, string deferralNumber, string customerName, string assignedRole);
    Task<bool> SendApprovalFlowStepUpdatedAsync(string toEmail, string userName, string deferralNumber, string customerName, string roleLabel);

    // ✅ NEW: Email verification and logout verification
    Task<bool> SendEmailVerificationCodeAsync(string toEmail, string verificationCode, int expiryMinutes);
    Task<bool> SendLogoutVerificationEmailAsync(string toEmail, string userName, string verificationCode, string ipAddress, string userAgent);
}