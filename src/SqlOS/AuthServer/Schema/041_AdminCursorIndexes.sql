-- SqlOS schema v41: admin cursor-pagination indexes.
-- Each keyset order ends in a unique tiebreaker so dashboard lists can seek
-- without OFFSET or a full-result COUNT(*). Column checks keep the script
-- safe on incomplete historical schemas used by in-place upgrades.

IF OBJECT_ID('[{Schema}].[SqlOSOrganizations]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSOrganizations]', 'Name') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSOrganizations_Name_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSOrganizations]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSOrganizations_Name_Id]
        ON [{Schema}].[SqlOSOrganizations]([Name], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSUsers]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSUsers]', 'DisplayName') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSUsers_DisplayName_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSUsers]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSUsers_DisplayName_Id]
        ON [{Schema}].[SqlOSUsers]([DisplayName], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSMemberships]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSMemberships]', 'UserId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSMemberships]', 'OrganizationId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSMemberships_UserId' AND object_id = OBJECT_ID('[{Schema}].[SqlOSMemberships]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSMemberships_UserId]
        ON [{Schema}].[SqlOSMemberships]([UserId], [OrganizationId]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSSessions]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSessions]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSSessions_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSSessions]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSSessions_CreatedAt_Id]
        ON [{Schema}].[SqlOSSessions]([CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSSessions]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSessions]', 'UserId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSessions]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSSessions_UserId_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSSessions]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSSessions_UserId_CreatedAt_Id]
        ON [{Schema}].[SqlOSSessions]([UserId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSClientApplications]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSClientApplications]', 'Name') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSClientApplications_Name_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSClientApplications]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSClientApplications_Name_Id]
        ON [{Schema}].[SqlOSClientApplications]([Name], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSAuthOidcConnections]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'DisplayName') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSAuthOidcConnections_DisplayName_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuthOidcConnections]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSAuthOidcConnections_DisplayName_Id]
        ON [{Schema}].[SqlOSAuthOidcConnections]([DisplayName], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSSsoConnections]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'DisplayName') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSSsoConnections_DisplayName_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSSsoConnections]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSSsoConnections_DisplayName_Id]
        ON [{Schema}].[SqlOSSsoConnections]([DisplayName], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSSsoConnections]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'OrganizationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'DisplayName') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSSsoConnections_OrganizationId_DisplayName_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSSsoConnections]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSSsoConnections_OrganizationId_DisplayName_Id]
        ON [{Schema}].[SqlOSSsoConnections]([OrganizationId], [DisplayName], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSInvitations]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSInvitations]', 'OrganizationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSInvitations]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSInvitations_OrganizationId_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSInvitations]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSInvitations_OrganizationId_CreatedAt_Id]
        ON [{Schema}].[SqlOSInvitations]([OrganizationId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSSsoPortalSessions]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSsoPortalSessions]', 'OrganizationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSSsoPortalSessions]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSSsoPortalSessions_OrganizationId_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSSsoPortalSessions]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSSsoPortalSessions_OrganizationId_CreatedAt_Id]
        ON [{Schema}].[SqlOSSsoPortalSessions]([OrganizationId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSAuditEvents]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'OccurredAt') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'IngestedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_OccurredAt_IngestedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSAuditEvents_OccurredAt_IngestedAt_Id]
        ON [{Schema}].[SqlOSAuditEvents]([OccurredAt] DESC, [IngestedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSEmailTemplates]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSEmailTemplates]', 'Key') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailTemplates_Key_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSEmailTemplates]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSEmailTemplates_Key_Id]
        ON [{Schema}].[SqlOSEmailTemplates]([Key], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSEmailDeliveries]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSEmailDeliveries]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSEmailDeliveries]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSEmailDeliveries_CreatedAt_Id]
        ON [{Schema}].[SqlOSEmailDeliveries]([CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSCalendarConnections]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSCalendarConnections]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSCalendarConnections_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSCalendarConnections]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSCalendarConnections_CreatedAt_Id]
        ON [{Schema}].[SqlOSCalendarConnections]([CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSApplicationAssignments]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSApplicationAssignments]', 'ClientApplicationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSApplicationAssignments]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSApplicationAssignments_Client_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSApplicationAssignments]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSApplicationAssignments_Client_CreatedAt_Id]
        ON [{Schema}].[SqlOSApplicationAssignments]([ClientApplicationId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSClientCredentials]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSClientCredentials]', 'ClientApplicationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSClientCredentials]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSClientCredentials_Client_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSClientCredentials]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSClientCredentials_Client_CreatedAt_Id]
        ON [{Schema}].[SqlOSClientCredentials]([ClientApplicationId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSScimConnections]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimConnections]', 'OrganizationId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimConnections]', 'DisplayName') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSScimConnections_OrganizationId_DisplayName_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSScimConnections]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSScimConnections_OrganizationId_DisplayName_Id]
        ON [{Schema}].[SqlOSScimConnections]([OrganizationId], [DisplayName], [Id]);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSScimGroupMappings]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimGroupMappings]', 'ConnectionId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimGroupMappings]', 'CreatedAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSScimGroupMappings_ConnectionId_CreatedAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSScimGroupMappings]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSScimGroupMappings_ConnectionId_CreatedAt_Id]
        ON [{Schema}].[SqlOSScimGroupMappings]([ConnectionId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF OBJECT_ID('[{Schema}].[SqlOSScimSyncEvents]', 'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimSyncEvents]', 'ConnectionId') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimSyncEvents]', 'OccurredAt') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSScimSyncEvents_ConnectionId_OccurredAt_Id' AND object_id = OBJECT_ID('[{Schema}].[SqlOSScimSyncEvents]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_SqlOSScimSyncEvents_ConnectionId_OccurredAt_Id]
        ON [{Schema}].[SqlOSScimSyncEvents]([ConnectionId], [OccurredAt] DESC, [Id] DESC);
END
GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (41);
