-- =============================================
-- ConcertTicketDB: tạo database + dữ liệu mẫu
-- Chạy 1 lần trong SSMS (hoặc sqlcmd -f 65001 -i ConcertTicketDB.sql)
-- Chạy lại sẽ XÓA database cũ và tạo mới
-- =============================================
USE master;
GO

IF DB_ID('ConcertTicketDB') IS NOT NULL
BEGIN
    ALTER DATABASE ConcertTicketDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE ConcertTicketDB;
END
GO

CREATE DATABASE ConcertTicketDB;
GO
USE ConcertTicketDB;
GO

-- người dùng đăng nhập bằng Google
CREATE TABLE [User] (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    GoogleSub   NVARCHAR(100) NOT NULL UNIQUE,   -- mã tài khoản Google, không đổi
    Email       NVARCHAR(200) NOT NULL UNIQUE,
    FullName    NVARCHAR(100) NOT NULL,
    AvatarUrl   NVARCHAR(500) NULL,
    CreatedAt   DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);

CREATE TABLE Concert (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(200) NOT NULL,
    Artist      NVARCHAR(200) NOT NULL,
    Venue       NVARCHAR(200) NOT NULL,
    Address     NVARCHAR(300) NOT NULL,
    StartTime   DATETIME2 NOT NULL,
    EndTime     DATETIME2 NOT NULL,
    PosterUrl   NVARCHAR(500) NULL,
    ZoneMapUrl  NVARCHAR(500) NULL,             -- ảnh sơ đồ khu vực
    Description NVARCHAR(1000) NULL,
    CONSTRAINT CK_Concert_Time CHECK (EndTime > StartTime)
);

-- khu vực: Trước sân khấu, Hai bên sân khấu, Khán đài...
CREATE TABLE Zone (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    ConcertId     INT NOT NULL,
    Name          NVARCHAR(100) NOT NULL,
    Price         DECIMAL(18,0) NOT NULL,
    TotalQuantity INT NOT NULL,
    SoldQuantity  INT NOT NULL DEFAULT 0,
    Gate          NVARCHAR(20) NOT NULL,
    CONSTRAINT FK_Zone_Concert FOREIGN KEY (ConcertId) REFERENCES Concert(Id),
    CONSTRAINT UQ_Zone_Name UNIQUE (ConcertId, Name),
    CONSTRAINT CK_Zone_Price CHECK (Price > 0),
    CONSTRAINT CK_Zone_Quantity CHECK (TotalQuantity > 0 AND SoldQuantity BETWEEN 0 AND TotalQuantity)
);

-- mỗi đơn mua 1 khu, 1..4 vé
CREATE TABLE [Order] (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    UserId          INT NOT NULL,
    ZoneId          INT NOT NULL,
    Quantity        INT NOT NULL,
    UnitPrice       DECIMAL(18,0) NOT NULL,     -- giá lúc mua
    TotalAmount     DECIMAL(18,0) NOT NULL,
    Status          NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    GoogleEventId   NVARCHAR(200) NULL,         -- 1 đơn = 1 sự kiện Calendar
    GoogleEventLink NVARCHAR(500) NULL,         -- htmlLink để mở sự kiện
    CreatedAt       DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    PaidAt          DATETIME2 NULL,
    CONSTRAINT FK_Order_User FOREIGN KEY (UserId) REFERENCES [User](Id),
    CONSTRAINT FK_Order_Zone FOREIGN KEY (ZoneId) REFERENCES Zone(Id),
    CONSTRAINT CK_Order_Quantity CHECK (Quantity BETWEEN 1 AND 4),
    CONSTRAINT CK_Order_Status CHECK (Status IN ('Pending', 'Paid', 'Failed'))
);

-- kết quả VNPay trả về, mỗi đơn tối đa 1 dòng
CREATE TABLE Payment (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    OrderId       INT NOT NULL UNIQUE,
    Provider      NVARCHAR(20) NOT NULL DEFAULT 'VNPay',
    Amount        DECIMAL(18,0) NOT NULL,
    TransactionNo NVARCHAR(50) NULL,            -- vnp_TransactionNo
    BankCode      NVARCHAR(20) NULL,            -- vnp_BankCode
    ResponseCode  NVARCHAR(10) NOT NULL,        -- '00' = thành công
    CreatedAt     DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Payment_Order FOREIGN KEY (OrderId) REFERENCES [Order](Id)
);

-- mỗi vé = 1 người vào cửa
CREATE TABLE Ticket (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    OrderId     INT NOT NULL,
    TicketCode  VARCHAR(30) NOT NULL UNIQUE,    -- CON-20261220-A7K9Q2
    Gate        NVARCHAR(20) NOT NULL,          -- chép từ Zone.Gate
    QrImageUrl  NVARCHAR(500) NULL,             -- link Cloudinary
    Status      NVARCHAR(20) NOT NULL DEFAULT 'Valid',
    CreatedAt   DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Ticket_Order FOREIGN KEY (OrderId) REFERENCES [Order](Id),
    CONSTRAINT CK_Ticket_Status CHECK (Status IN ('Valid', 'Used'))
);
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.PaymentCheckout', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentCheckout (
        OrderId INT NOT NULL CONSTRAINT PK_PaymentCheckout PRIMARY KEY,
        TxnRef NVARCHAR(100) NOT NULL,
        TmnCode NVARCHAR(8) NOT NULL,
        Amount DECIMAL(18,0) NOT NULL,
        CreateDate NVARCHAR(14) NOT NULL,
        ExpireDate NVARCHAR(14) NOT NULL,
        ReturnUrl NVARCHAR(255) NOT NULL,
        IpAddress NVARCHAR(45) NOT NULL,
        NextQueryAt DATETIMEOFFSET NULL,
        CONSTRAINT FK_PaymentCheckout_Order FOREIGN KEY (OrderId) REFERENCES dbo.[Order](Id),
        CONSTRAINT UQ_PaymentCheckout_TxnRef UNIQUE (TxnRef)
    );
END;

IF OBJECT_ID(N'dbo.PaymentActivity', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentActivity (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PaymentActivity PRIMARY KEY,
        OrderId INT NULL,
        OccurredAt DATETIMEOFFSET NOT NULL,
        EventKey NVARCHAR(120) NOT NULL,
        EventType NVARCHAR(50) NOT NULL,
        Outcome NVARCHAR(10) NOT NULL,
        Details NVARCHAR(300) NOT NULL,
        CONSTRAINT FK_PaymentActivity_Order FOREIGN KEY (OrderId) REFERENCES dbo.[Order](Id),
        CONSTRAINT UQ_PaymentActivity_EventKey UNIQUE (EventKey),
        CONSTRAINT CK_PaymentActivity_Outcome CHECK (Outcome IN ('Pending','Success','Failed','Warning'))
    );
    CREATE INDEX IX_PaymentActivity_OrderTime ON dbo.PaymentActivity(OrderId, OccurredAt, Id);
END;

COMMIT TRANSACTION;
GO

-- ===== DỮ LIỆU MẪU =====
INSERT INTO Concert (Name, Artist, Venue, Address, StartTime, EndTime, Description) VALUES
(N'Đêm Nhạc Mùa Đông 2026', N'Nhiều nghệ sĩ', N'Nhà thi đấu Phú Thọ', N'1 Lữ Gia, Quận 11, TP.HCM',
    '2026-12-20 19:30', '2026-12-20 22:30', N'Đêm nhạc pop ballad cuối năm.'),
(N'Rock Saigon Fest', N'Các ban nhạc rock Việt', N'Sân vận động Quân khu 7', N'202 Hoàng Văn Thụ, Tân Bình, TP.HCM',
    '2027-01-10 18:00', '2027-01-10 23:00', N'Lễ hội rock ngoài trời.'),
(N'Acoustic Night: Kể Chuyện Mùa Xuân', N'Nhiều nghệ sĩ', N'Nhà hát Hòa Bình', N'240 Đường 3 Tháng 2, Quận 10, TP.HCM',
    '2027-02-14 20:00', '2027-02-14 22:00', N'Đêm acoustic nhân ngày Valentine.');

INSERT INTO Zone (ConcertId, Name, Price, TotalQuantity, SoldQuantity, Gate) VALUES
(1, N'Trước sân khấu',   2500000,  200,   0, N'Gate A'),
(1, N'Hai bên sân khấu', 1500000,  400,   0, N'Gate B'),
(1, N'Khán đài',          800000, 1000,   0, N'Gate C'),
(2, N'Trước sân khấu',   1800000,  300,   0, N'Gate A'),
(2, N'Hai bên sân khấu', 1200000,  600,   0, N'Gate B'),
(2, N'Khán đài A',        600000, 1500,   0, N'Gate C'),
(2, N'Khán đài B',        600000, 1500,   0, N'Gate D'),
(3, N'Trước sân khấu',   1200000,  100, 100, N'Gate A'),   -- hết vé, để test
(3, N'Hai bên sân khấu',  900000,  150,   0, N'Gate B'),
(3, N'Ban công',           500000,  250,   0, N'Gate C');
GO
