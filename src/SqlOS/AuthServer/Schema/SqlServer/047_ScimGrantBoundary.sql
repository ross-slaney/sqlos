-- A SCIM connection declares the FGA resource whose subtree bounds every grant its
-- group mappings may create. Existing connections keep NULL, which fails closed:
-- a connection without a boundary creates no mapped grants until an operator sets one.
--
-- No foreign key to the FGA resource table: the AuthServer schema is applied before
-- the FGA schema on a fresh database, and the FGA resource table name is configurable
-- (SqlOSFgaOptions.TableNames). SqlOS validates the boundary resource when it is saved
-- and again before every mapped grant, so a missing boundary resource also fails closed.

IF OBJECT_ID(N'[{Schema}].[SqlOSScimConnections]', N'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSScimConnections]', 'GrantBoundaryResourceId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimConnections]
    ADD [GrantBoundaryResourceId] NVARCHAR(256) NULL;
END
