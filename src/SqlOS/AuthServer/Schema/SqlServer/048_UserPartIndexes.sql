-- SqlOS schema v48: an index on UserId for every table the user aggregate loads by user.
-- The aggregate loads each part it changes whole: every live credential, every email address
-- and every external identity of one account. Sign-in reads credentials and email addresses
-- this way, and claiming an address reads external identities. The phone number,
-- authenticator, recovery code and MFA override tables already had a UserId index; these
-- three did not, so each of those reads scanned the whole table and a sign-in grew slower
-- with every account. The EF model has always declared these indexes. Column checks keep
-- the script safe on incomplete historical schemas used by in-place upgrades.

IF OBJECT_ID(N'[{Schema}].[SqlOSCredentials]', N'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSCredentials]', 'UserId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_SqlOSCredentials_UserId' AND [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSCredentials]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSCredentials_UserId]
        ON [{Schema}].[SqlOSCredentials]([UserId]);
END
GO

IF OBJECT_ID(N'[{Schema}].[SqlOSUserEmails]', N'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSUserEmails]', 'UserId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_SqlOSUserEmails_UserId' AND [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSUserEmails]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSUserEmails_UserId]
        ON [{Schema}].[SqlOSUserEmails]([UserId]);
END
GO

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'UserId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_SqlOSExternalIdentities_UserId' AND [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSExternalIdentities_UserId]
        ON [{Schema}].[SqlOSExternalIdentities]([UserId]);
END
GO
