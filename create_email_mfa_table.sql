CREATE TABLE IF NOT EXISTS `EmailMFACodes` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `Code` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `SessionToken` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `IsUsed` tinyint(1) NOT NULL DEFAULT 0,
    `VerifiedAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `ExpiresAt` datetime(6) NOT NULL,
    `IpAddress` longtext CHARACTER SET utf8mb4 NULL,
    `UserAgent` longtext CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`Id`),
    FOREIGN KEY (`UserId`) REFERENCES `Users`(`Id`) ON DELETE CASCADE,
    KEY `IX_EmailMFACodes_UserId` (`UserId`)
) CHARACTER SET utf8mb4;
