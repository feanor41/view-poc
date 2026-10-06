-- Bootstrap only; explicit object creation is idempotent. Passwords are generated as a fixed complexity prefix plus 64 hex characters.
USE master;
GO
IF DB_ID(N'Accounts') IS NULL CREATE DATABASE [Accounts];
GO
ALTER DATABASE [Accounts] SET TRUSTWORTHY OFF;
ALTER DATABASE [Accounts] SET DB_CHAINING OFF;
GO
IF SUSER_ID(N'vwp_accounts') IS NULL CREATE LOGIN [vwp_accounts] WITH PASSWORD = '$(AccountsPassword)', CHECK_POLICY = ON;
GO
IF DB_ID(N'Financials') IS NULL CREATE DATABASE [Financials];
GO
ALTER DATABASE [Financials] SET TRUSTWORTHY OFF;
ALTER DATABASE [Financials] SET DB_CHAINING OFF;
GO
IF SUSER_ID(N'vwp_financials') IS NULL CREATE LOGIN [vwp_financials] WITH PASSWORD = '$(FinancialsPassword)', CHECK_POLICY = ON;
GO
IF DB_ID(N'Cases') IS NULL CREATE DATABASE [Cases];
GO
ALTER DATABASE [Cases] SET TRUSTWORTHY OFF;
ALTER DATABASE [Cases] SET DB_CHAINING OFF;
GO
IF SUSER_ID(N'vwp_cases') IS NULL CREATE LOGIN [vwp_cases] WITH PASSWORD = '$(CasesPassword)', CHECK_POLICY = ON;
GO
IF DB_ID(N'Notifications') IS NULL CREATE DATABASE [Notifications];
GO
ALTER DATABASE [Notifications] SET TRUSTWORTHY OFF;
ALTER DATABASE [Notifications] SET DB_CHAINING OFF;
GO
IF SUSER_ID(N'vwp_notifications') IS NULL CREATE LOGIN [vwp_notifications] WITH PASSWORD = '$(NotificationsPassword)', CHECK_POLICY = ON;
GO
USE Accounts;
GO
IF OBJECT_ID(N'dbo.Accounts',N'U') IS NULL CREATE TABLE dbo.[Accounts] (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(200) NOT NULL, Status nvarchar(40) NOT NULL, CreatedAt datetimeoffset(7) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Assets',N'U') IS NULL CREATE TABLE dbo.[Assets] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL REFERENCES dbo.Accounts(Id), Name nvarchar(200) NOT NULL, Kind nvarchar(80) NOT NULL, Value decimal(18,2) NOT NULL);
GO
IF OBJECT_ID(N'dbo.ContactInformation',N'U') IS NULL CREATE TABLE dbo.[ContactInformation] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL UNIQUE REFERENCES dbo.Accounts(Id), Email nvarchar(320) NOT NULL, Phone nvarchar(50) NOT NULL);
GO
IF OBJECT_ID(N'dbo.AccountHistory',N'U') IS NULL CREATE TABLE dbo.[AccountHistory] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL REFERENCES dbo.Accounts(Id), Description nvarchar(1000) NOT NULL, OccurredAt datetimeoffset(7) NOT NULL);
GO
IF USER_ID(N'vwp_accounts') IS NULL CREATE USER [vwp_accounts] FOR LOGIN [vwp_accounts];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [vwp_accounts];
GO
IF USER_ID(N'vwp_financials') IS NULL CREATE USER [vwp_financials] FOR LOGIN [vwp_financials];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_financials];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_financials];
GRANT SELECT ON OBJECT::dbo.[Assets] TO [vwp_financials];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Assets] TO [vwp_financials];
GO
IF USER_ID(N'vwp_cases') IS NULL CREATE USER [vwp_cases] FOR LOGIN [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[Assets] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Assets] TO [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[ContactInformation] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[ContactInformation] TO [vwp_cases];
GO
IF USER_ID(N'vwp_notifications') IS NULL CREATE USER [vwp_notifications] FOR LOGIN [vwp_notifications];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_notifications];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_notifications];
GRANT SELECT ON OBJECT::dbo.[ContactInformation] TO [vwp_notifications];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[ContactInformation] TO [vwp_notifications];
GO
USE [Financials];
GO
IF OBJECT_ID(N'dbo.PaymentMeans',N'U') IS NULL CREATE TABLE dbo.[PaymentMeans] (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Operations',N'U') IS NULL CREATE TABLE dbo.[Operations] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL, Description nvarchar(500) NOT NULL, Amount decimal(18,2) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Transactions',N'U') IS NULL CREATE TABLE dbo.[Transactions] (Id uniqueidentifier NOT NULL PRIMARY KEY, OperationId uniqueidentifier NOT NULL REFERENCES dbo.Operations(Id), PaymentMeansId uniqueidentifier NOT NULL REFERENCES dbo.PaymentMeans(Id), Amount decimal(18,2) NOT NULL);
GO
IF OBJECT_ID(N'dbo.CollectionOrders',N'U') IS NULL CREATE TABLE dbo.[CollectionOrders] (Id uniqueidentifier NOT NULL PRIMARY KEY, OperationId uniqueidentifier NOT NULL REFERENCES dbo.Operations(Id), DueAt datetimeoffset(7) NOT NULL);
GO
CREATE OR ALTER VIEW dbo.[Accounts] AS SELECT Id, Name, Status, CreatedAt FROM [Accounts].dbo.[Accounts];
GO
CREATE OR ALTER VIEW dbo.[Assets] AS SELECT Id, AccountId, Name, Kind, Value FROM [Accounts].dbo.[Assets];
GO
IF USER_ID(N'vwp_financials') IS NULL CREATE USER [vwp_financials] FOR LOGIN [vwp_financials];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[PaymentMeans] TO [vwp_financials];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[Operations] TO [vwp_financials];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[Transactions] TO [vwp_financials];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[CollectionOrders] TO [vwp_financials];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_financials];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_financials];
GRANT SELECT ON OBJECT::dbo.[Assets] TO [vwp_financials];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Assets] TO [vwp_financials];
GO
USE [Cases];
GO
IF OBJECT_ID(N'dbo.CaseTypes',N'U') IS NULL CREATE TABLE dbo.[CaseTypes] (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
GO
IF OBJECT_ID(N'dbo.CaseGroup',N'U') IS NULL CREATE TABLE dbo.[CaseGroup] (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Cases',N'U') IS NULL CREATE TABLE dbo.[Cases] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL, AssetId uniqueidentifier NULL, CaseTypeId uniqueidentifier NOT NULL REFERENCES dbo.CaseTypes(Id), CaseGroupId uniqueidentifier NOT NULL REFERENCES dbo.CaseGroup(Id), Title nvarchar(200) NOT NULL);
GO
CREATE OR ALTER VIEW dbo.[Accounts] AS SELECT Id, Name, Status, CreatedAt FROM [Accounts].dbo.[Accounts];
GO
CREATE OR ALTER VIEW dbo.[Assets] AS SELECT Id, AccountId, Name, Kind, Value FROM [Accounts].dbo.[Assets];
GO
CREATE OR ALTER VIEW dbo.[ContactInformation] AS SELECT Id, AccountId, Email, Phone FROM [Accounts].dbo.[ContactInformation];
GO
IF USER_ID(N'vwp_cases') IS NULL CREATE USER [vwp_cases] FOR LOGIN [vwp_cases];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[CaseTypes] TO [vwp_cases];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[CaseGroup] TO [vwp_cases];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[Cases] TO [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[Assets] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Assets] TO [vwp_cases];
GRANT SELECT ON OBJECT::dbo.[ContactInformation] TO [vwp_cases];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[ContactInformation] TO [vwp_cases];
GO
USE [Notifications];
GO
IF OBJECT_ID(N'dbo.NotificationTypes',N'U') IS NULL CREATE TABLE dbo.[NotificationTypes] (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Notifications',N'U') IS NULL CREATE TABLE dbo.[Notifications] (Id uniqueidentifier NOT NULL PRIMARY KEY, AccountId uniqueidentifier NOT NULL, NotificationTypeId uniqueidentifier NOT NULL REFERENCES dbo.NotificationTypes(Id), Message nvarchar(1000) NOT NULL);
GO
CREATE OR ALTER VIEW dbo.[Accounts] AS SELECT Id, Name, Status, CreatedAt FROM [Accounts].dbo.[Accounts];
GO
CREATE OR ALTER VIEW dbo.[ContactInformation] AS SELECT Id, AccountId, Email, Phone FROM [Accounts].dbo.[ContactInformation];
GO
IF USER_ID(N'vwp_notifications') IS NULL CREATE USER [vwp_notifications] FOR LOGIN [vwp_notifications];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[NotificationTypes] TO [vwp_notifications];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[Notifications] TO [vwp_notifications];
GRANT SELECT ON OBJECT::dbo.[Accounts] TO [vwp_notifications];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[Accounts] TO [vwp_notifications];
GRANT SELECT ON OBJECT::dbo.[ContactInformation] TO [vwp_notifications];
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.[ContactInformation] TO [vwp_notifications];
GO
