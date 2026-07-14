IF OBJECT_ID('[{Schema}].[SqlOSSamlReplays]', 'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSamlReplays] (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSSamlReplays] PRIMARY KEY,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [ResponseId] NVARCHAR(450) NOT NULL,
        [AssertionId] NVARCHAR(450) NOT NULL,
        [ConsumedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [UX_SqlOSSamlReplays_Connection_Response]
        ON [{Schema}].[SqlOSSamlReplays]([ConnectionId], [ResponseId]);
    CREATE UNIQUE INDEX [UX_SqlOSSamlReplays_Connection_Assertion]
        ON [{Schema}].[SqlOSSamlReplays]([ConnectionId], [AssertionId]);
    CREATE INDEX [IX_SqlOSSamlReplays_ExpiresAt]
        ON [{Schema}].[SqlOSSamlReplays]([ExpiresAt]);
END

UPDATE [{Schema}].[SqlOSSchema] SET [Version] = 31;
IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (31);
END
