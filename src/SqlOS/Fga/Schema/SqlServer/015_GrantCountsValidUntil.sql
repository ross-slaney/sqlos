-- SqlOSFga Schema v15: when a principal's grant counts fall behind the clock.
--
-- A grant whose window opens or closes later changes nothing in the database by itself, so the counts of
-- its principal are right only until that moment. The principal's root row carries the next such moment
-- (ValidUntil); a page whose caller has passed it rebuilds the caller's counts before walking, and the
-- hosted refresh service does the same ahead of time. A principal with no usable grant yet but a window
-- that will open has a root row with no counts of its own and that moment.

IF COL_LENGTH(N'[{Schema}].[SqlOSFgaGrantCounts]', N'ValidUntil') IS NULL
    ALTER TABLE [{Schema}].[SqlOSFgaGrantCounts] ADD [ValidUntil] DATETIME2 NULL;
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 15 WHERE [Version] < 15;
GO
