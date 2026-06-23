IF OBJECT_ID('[{Schema}].[SqlOSSigningKeys]', 'U') IS NOT NULL
BEGIN
    DECLARE @SigningKeyInvariantNow DATETIME2 = SYSUTCDATETIME();

    ;WITH RankedActiveKeys AS (
        SELECT
            [Id],
            ROW_NUMBER() OVER (ORDER BY [ActivatedAt] DESC, [Id] DESC) AS [Rank]
        FROM [{Schema}].[SqlOSSigningKeys]
        WHERE [IsActive] = 1
    )
    UPDATE [SigningKeys]
    SET
        [IsActive] = 0,
        [RetiredAt] = COALESCE([SigningKeys].[RetiredAt], @SigningKeyInvariantNow)
    FROM [{Schema}].[SqlOSSigningKeys] AS [SigningKeys]
    INNER JOIN [RankedActiveKeys] AS [Ranked]
        ON [Ranked].[Id] = [SigningKeys].[Id]
    WHERE [Ranked].[Rank] > 1;

    UPDATE [{Schema}].[SqlOSSigningKeys]
    SET [RetiredAt] = NULL
    WHERE [IsActive] = 1
      AND [RetiredAt] IS NOT NULL;

    UPDATE [{Schema}].[SqlOSSigningKeys]
    SET [RetiredAt] = COALESCE([ActivatedAt], @SigningKeyInvariantNow)
    WHERE [IsActive] = 0
      AND [RetiredAt] IS NULL;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = 'UX_SqlOSSigningKeys_Active'
          AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSSigningKeys]')
    )
    BEGIN
        CREATE UNIQUE INDEX [UX_SqlOSSigningKeys_Active]
            ON [{Schema}].[SqlOSSigningKeys] ([IsActive])
            WHERE [IsActive] = 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE [name] = 'CK_SqlOSSigningKeys_Lifecycle'
          AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSSigningKeys]')
    )
    BEGIN
        ALTER TABLE [{Schema}].[SqlOSSigningKeys]
            ADD CONSTRAINT [CK_SqlOSSigningKeys_Lifecycle]
            CHECK (
                ([IsActive] = CONVERT(bit, 1) AND [RetiredAt] IS NULL)
                OR ([IsActive] = CONVERT(bit, 0) AND [RetiredAt] IS NOT NULL)
            );
    END
END

IF EXISTS (SELECT * FROM [{Schema}].[SqlOSSchema])
    UPDATE [{Schema}].[SqlOSSchema] SET [Version] = 26;
ELSE
    INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (26);
