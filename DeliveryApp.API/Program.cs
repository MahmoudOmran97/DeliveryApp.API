using DeliveryApp.API.Hubs;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IHubService, HubService>();
builder.Services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
builder.Services.AddHttpClient("fcm");
builder.Services.AddScoped<IFcmService, FcmService>();
builder.Services.AddHttpClient("cloudinary");
builder.Services.AddScoped<IImageStorageService, CloudinaryStorageService>();
builder.Services.AddScoped<IPointsService, PointsService>();

// ✅ الجديد: خدمات الـ OTP (إرسال إيميل + توليد/تحقق الكود)
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IAiSupportService, AiSupportService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT Key is not configured");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                context.Token = accessToken;
            return Task.CompletedTask;
        },

        // ✅ فحص حالة الحساب مع كل توكن بيتحقق منه (سواء API عادي أو اتصال SignalR).
        // لو الأدمن قفل الحساب، أي طلب جاي بالتوكن ده هيترفض فوراً بكود مميز
        // (ACCOUNT_DEACTIVATED) عشان الأبليكيشن عند العميل يعرف يعمل logout فوري،
        // حتى لو مكنش مستني إشعار SignalR/FCM.
        OnTokenValidated = async context =>
        {
            var userIdClaim = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
                             ?? context.Principal?.FindFirst("sub");

            if (userIdClaim != null && int.TryParse(userIdClaim.Value, out var userId))
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                var isActive = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => (bool?)u.IsActive)
                    .FirstOrDefaultAsync();

                if (isActive != true)
                {
                    context.HttpContext.Items["AccountDeactivated"] = true;
                    context.Fail("Account is deactivated");
                }
            }
        },

        OnChallenge = async context =>
        {
            if (context.HttpContext.Items.ContainsKey("AccountDeactivated"))
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"code\":\"ACCOUNT_DEACTIVATED\",\"message\":\"Your account has been deactivated\"}");
            }
        }
    };
});

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Delivery API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter JWT Token"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// BUG FIX B: عمود ChatMessages مش موجود في الداتابيز
// المفروض يتعمل عن طريق DbContext لكن migration
// بدلاً: عمل CREATE TABLE IF NOT EXISTS جوا startup
// ─────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'Restaurants' AND COLUMN_NAME = 'OwnerUserId'
            )
            BEGIN
                ALTER TABLE [dbo].[Restaurants] ADD [OwnerUserId] INT NULL;
                IF NOT EXISTS (
                    SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Restaurants_OwnerUser'
                )
                ALTER TABLE [dbo].[Restaurants] ADD CONSTRAINT [FK_Restaurants_OwnerUser]
                    FOREIGN KEY ([OwnerUserId]) REFERENCES [Users]([Id]);
            END
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Restaurants OwnerUserId check failed: {ex.Message}");
    }
    // ── Create UserCoupons table ──────────────────────────────────────────────
    try
    {
        // تنفيذ استعلام SQL خام للتحقق من وجود الجدول وإنشائه إذا لزم الأمر
        await db.Database.ExecuteSqlRawAsync(@"
        IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserCoupons')
        BEGIN
            CREATE TABLE [dbo].[UserCoupons] (
                [Id]        INT       IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [UserId]    INT       NOT NULL,
                [CouponId]  INT       NOT NULL,
                [UsedAt]    DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                
                -- إضافة القيود (Constraints) لربط الجدول بالجداول الأخرى
                CONSTRAINT [FK_UserCoupons_Users]
                    FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE,
                
                CONSTRAINT [FK_UserCoupons_Coupons]
                    FOREIGN KEY ([CouponId]) REFERENCES [Coupons]([Id]) ON DELETE CASCADE
            );

            -- إنشاء فهارس (Indexes) لتحسين أداء الاستعلامات
            CREATE INDEX [IX_UserCoupons_UserId] ON [dbo].[UserCoupons]([UserId]);
            CREATE INDEX [IX_UserCoupons_CouponId] ON [dbo].[UserCoupons]([CouponId]);
        END
    ");
        Console.WriteLine("[Startup] UserCoupons table ready.");
    }
    catch (Exception ex)
    {
        // تسجيل الخطأ في حال فشل العملية لضمان عدم توقف التطبيق بالكامل
        Console.WriteLine($"[Startup] UserCoupons table check failed: {ex.Message}");
    }

    // ── Fix Role CHECK constraint ─────────────────────────────────────────
    // Drop any old CHECK constraint on Role and recreate with correct values
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DECLARE @constraintName NVARCHAR(200);
            SELECT @constraintName = name 
            FROM sys.check_constraints 
            WHERE parent_object_id = OBJECT_ID('dbo.Users')
              AND OBJECT_NAME(parent_object_id) = 'Users'
              AND definition LIKE '%Role%';

            IF @constraintName IS NOT NULL
            BEGIN
                -- Check if the constraint already allows 'Restaurant'
                DECLARE @def NVARCHAR(500);
                SELECT @def = definition 
                FROM sys.check_constraints 
                WHERE name = @constraintName;

                IF @def NOT LIKE '%Restaurant%'
                BEGIN
                    EXEC('ALTER TABLE [dbo].[Users] DROP CONSTRAINT [' + @constraintName + ']');
                    ALTER TABLE [dbo].[Users] ADD CONSTRAINT [CK_Users_Role]
                        CHECK ([Role] IN ('Admin', 'Restaurant', 'Driver', 'Customer'));
                END
            END
        ");
        Console.WriteLine("[Startup] Role constraint check passed.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Role constraint fix failed: {ex.Message}");
    }

    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = 'ChatMessages'
            )
            BEGIN
                CREATE TABLE [dbo].[ChatMessages] (
                    [Id]        INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [OrderId]   INT            NOT NULL,
                    [SenderId]  INT            NOT NULL,
                    [Message]   NVARCHAR(1000) NOT NULL,
                    [Timestamp] DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_ChatMessages_Orders]
                        FOREIGN KEY ([OrderId]) REFERENCES [Orders]([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_ChatMessages_Users]
                        FOREIGN KEY ([SenderId]) REFERENCES [Users]([Id])
                );
                CREATE INDEX [IX_ChatMessages_OrderId]  ON [dbo].[ChatMessages]([OrderId]);
                CREATE INDEX [IX_ChatMessages_SenderId] ON [dbo].[ChatMessages]([SenderId]);
            END
        ");
    }
    catch (Exception ex)
    {
        // log only – don't crash the app
        Console.WriteLine($"[Startup] ChatMessages table check failed: {ex.Message}");
    }
    // ── Create Banners table ──────────────────────────────────────────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Banners')
            BEGIN
                CREATE TABLE [dbo].[Banners] (
                    [Id]              INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Title]           NVARCHAR(200)  NOT NULL,
                    [SubTitle]        NVARCHAR(500)  NULL,
                    [ImageUrl]        NVARCHAR(500)  NULL,
                    [ActionUrl]       NVARCHAR(300)  NULL,
                    [BackgroundColor] NVARCHAR(50)   NULL,
                    [SortOrder]       INT            NOT NULL DEFAULT 0,
                    [IsActive]        BIT            NOT NULL DEFAULT 1,
                    [StartsAt]        DATETIME2      NULL,
                    [EndsAt]          DATETIME2      NULL,
                    [CreatedAt]       DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                -- Seed sample banners
                INSERT INTO [dbo].[Banners] ([Title],[SubTitle],[ImageUrl],[BackgroundColor],[SortOrder],[IsActive])
                VALUES 
                (N'اطلب وادخر', N'أفضل عروض المطاعم بين إيدك', NULL, '#FF5722', 1, 1),
                (N'توصيل سريع', N'توصيل لحد بيتك في أقل من 30 دقيقة', NULL, '#6200EA', 2, 1),
                (N'عروض خاصة', N'خصومات حصرية على منتجات مختارة', NULL, '#00897B', 3, 1);
            END
        ");
        Console.WriteLine("[Startup] Banners table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Banners table check failed: {ex.Message}"); }

    // ── Create Coupons table ──────────────────────────────────────────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Coupons')
            BEGIN
                CREATE TABLE [dbo].[Coupons] (
                    [Id]              INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Code]            NVARCHAR(50)   NOT NULL UNIQUE,
                    [Title]           NVARCHAR(200)  NOT NULL,
                    [Description]     NVARCHAR(500)  NULL,
                    [DiscountType]    NVARCHAR(20)   NOT NULL DEFAULT 'Fixed',
                    [DiscountValue]   DECIMAL(10,2)  NOT NULL,
                    [MinOrderAmount]  DECIMAL(10,2)  NULL,
                    [MaxDiscount]     DECIMAL(10,2)  NULL,
                    [RestaurantId]    INT            NULL,
                    [UsageLimit]      INT            NULL,
                    [UsedCount]       INT            NOT NULL DEFAULT 0,
                    [IsActive]        BIT            NOT NULL DEFAULT 1,
                    [ExpiresAt]       DATETIME2      NULL,
                    [CreatedAt]       DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                -- Seed sample coupons
                INSERT INTO [dbo].[Coupons] ([Code],[Title],[Description],[DiscountType],[DiscountValue],[MinOrderAmount],[IsActive],[ExpiresAt])
                VALUES 
                (N'SAVE20', N'خصم 20 جنيه', N'خصم 20 جنيه على أي طلب فوق 100 جنيه', 'Fixed', 20, 100, 1, DATEADD(MONTH,3,GETUTCDATE())),
                (N'FIRST50', N'خصم 50% للمرة الأولى', N'خصم 50% على أول طلب لك', 'Percentage', 50, 50, 1, DATEADD(MONTH,1,GETUTCDATE())),
                (N'FREE', N'توصيل مجاني', N'احصل على توصيل مجاني لأي طلب', 'Fixed', 15, 80, 1, DATEADD(MONTH,2,GETUTCDATE()));
            END
        ");
        Console.WriteLine("[Startup] Coupons table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Coupons table check failed: {ex.Message}"); }

    // ── Coupons.OwnerUserId — يقصر الكوبون الناتج عن استبدال النقاط على صاحبه بس ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'Coupons' AND COLUMN_NAME = 'OwnerUserId'
            )
            BEGIN
                ALTER TABLE [dbo].[Coupons] ADD [OwnerUserId] INT NULL;
                IF NOT EXISTS (
                    SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Coupons_OwnerUser'
                )
                ALTER TABLE [dbo].[Coupons] ADD CONSTRAINT [FK_Coupons_OwnerUser]
                    FOREIGN KEY ([OwnerUserId]) REFERENCES [Users]([Id]);
            END
        ");
        Console.WriteLine("[Startup] Coupons.OwnerUserId ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Coupons.OwnerUserId check failed: {ex.Message}"); }

    // ── Create PrescriptionRequests + PrescriptionMessages tables ──────────────
    // شات ما قبل الأوردر بين العميل وصاحب الصيدلية عشان يتفقوا على تمن الروشتة
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'PrescriptionRequests')
            BEGIN
                CREATE TABLE [dbo].[PrescriptionRequests] (
                    [Id]             INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [CustomerId]     INT            NOT NULL,
                    [RestaurantId]   INT            NOT NULL,
                    [ImageUrl]       NVARCHAR(500)  NOT NULL,
                    [Notes]          NVARCHAR(1000) NULL,
                    [Status]         NVARCHAR(20)   NOT NULL DEFAULT 'Pending',
                    [AgreedPrice]    DECIMAL(10,2)  NULL,
                    [OrderId]        INT            NULL,
                    [CreatedAt]      DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
                    [PricedAt]       DATETIME2      NULL,
                    [ConfirmedAt]    DATETIME2      NULL,
                    CONSTRAINT [FK_PrescriptionRequests_Customer]
                        FOREIGN KEY ([CustomerId]) REFERENCES [Users]([Id]),
                    CONSTRAINT [FK_PrescriptionRequests_Restaurant]
                        FOREIGN KEY ([RestaurantId]) REFERENCES [Restaurants]([Id])
                );
                CREATE INDEX [IX_PrescriptionRequests_CustomerId] ON [dbo].[PrescriptionRequests]([CustomerId]);
                CREATE INDEX [IX_PrescriptionRequests_RestaurantId] ON [dbo].[PrescriptionRequests]([RestaurantId]);
            END

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'PrescriptionMessages')
            BEGIN
                CREATE TABLE [dbo].[PrescriptionMessages] (
                    [Id]                     INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PrescriptionRequestId]  INT            NOT NULL,
                    [SenderId]               INT            NOT NULL,
                    [SenderRole]             NVARCHAR(20)   NOT NULL DEFAULT 'Customer',
                    [Message]                NVARCHAR(1000) NOT NULL,
                    [CreatedAt]              DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_PrescriptionMessages_Request]
                        FOREIGN KEY ([PrescriptionRequestId]) REFERENCES [PrescriptionRequests]([Id]) ON DELETE CASCADE
                );
                CREATE INDEX [IX_PrescriptionMessages_RequestId] ON [dbo].[PrescriptionMessages]([PrescriptionRequestId]);
            END
        ");
        Console.WriteLine("[Startup] PrescriptionRequests/Messages tables ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] PrescriptionRequests tables check failed: {ex.Message}"); }

    // ── Orders.PrescriptionRequestId — ربط الأوردر بطلب الروشتة اللي جه منه ───
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'Orders' AND COLUMN_NAME = 'PrescriptionRequestId'
            )
            BEGIN
                ALTER TABLE [dbo].[Orders] ADD [PrescriptionRequestId] INT NULL;
            END
        ");
        Console.WriteLine("[Startup] Orders.PrescriptionRequestId ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Orders.PrescriptionRequestId check failed: {ex.Message}"); }

    // ── Create Deals table ────────────────────────────────────────────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Deals')
            BEGIN
                CREATE TABLE [dbo].[Deals] (
                    [Id]              INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Title]           NVARCHAR(200)  NOT NULL,
                    [Description]     NVARCHAR(500)  NULL,
                    [ImageUrl]        NVARCHAR(500)  NULL,
                    [RestaurantId]    INT            NULL,
                    [ProductId]       INT            NULL,
                    [OriginalPrice]   DECIMAL(10,2)  NULL,
                    [DiscountedPrice] DECIMAL(10,2)  NULL,
                    [DiscountPercent] INT            NULL,
                    [BadgeText]       NVARCHAR(50)   NULL,
                    [BadgeColor]      NVARCHAR(50)   NULL,
                    [IsActive]        BIT            NOT NULL DEFAULT 1,
                    [SortOrder]       INT            NOT NULL DEFAULT 0,
                    [ExpiresAt]       DATETIME2      NULL,
                    [CreatedAt]       DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                -- Seed sample deals
                INSERT INTO [dbo].[Deals] ([Title],[Description],[OriginalPrice],[DiscountedPrice],[DiscountPercent],[BadgeText],[BadgeColor],[IsActive],[SortOrder])
                VALUES 
                (N'وجبة برجر ممتازة', N'برجر مع بطاطس ومشروب', 89, 59, 34, N'خصم 34%', N'#F44336', 1, 1),
                (N'بيتزا كبيرة', N'بيتزا كبيرة أي نوع', 120, 85, 29, N'خصم 29%', N'#FF9800', 1, 2),
                (N'وجبة شاورما', N'شاورما دجاج مع إضافات', 65, 45, 31, N'عرض محدود', N'#4CAF50', 1, 3),
                (N'سندوتش فراخ مقرمش', N'مع صوص خاص وبطاطس', 55, 39, 29, N'خصم 29%', N'#9C27B0', 1, 4);
            END
        ");
        Console.WriteLine("[Startup] Deals table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Deals table check failed: {ex.Message}"); }

    // ── Add StoreType column to Restaurants ───────────────────────────────────
    // القيم: Restaurants | Pharmacy | Grocery | Supermarket | Vegetables | Drinks | Accessories
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'Restaurants' AND COLUMN_NAME = 'StoreType'
            )
            BEGIN
                ALTER TABLE [dbo].[Restaurants]
                ADD [StoreType] NVARCHAR(50) NOT NULL DEFAULT 'Restaurants';
            END
            ELSE
            BEGIN
                -- صلّح الـ rows القديمة اللي اتضافت بـ default غلط 'Restaurant' (مفرد)
                UPDATE [dbo].[Restaurants]
                SET [StoreType] = 'Restaurants'
                WHERE [StoreType] = 'Restaurant';
            END
        ");
        Console.WriteLine("[Startup] Restaurants.StoreType column ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] StoreType column check failed: {ex.Message}"); }

    // ── Add PreferredLanguage column to Users ─────────────────────────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'Users' AND COLUMN_NAME = 'PreferredLanguage'
            )
            BEGIN
                ALTER TABLE [dbo].[Users]
                ADD [PreferredLanguage] NVARCHAR(5) NOT NULL DEFAULT 'en';
            END
        ");
        Console.WriteLine("[Startup] Users.PreferredLanguage column ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] PreferredLanguage column check failed: {ex.Message}"); }

    // ── ProductVariants table ───────────────────────────────────────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ProductVariants')
            BEGIN
                CREATE TABLE [dbo].[ProductVariants] (
                    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [ProductId] INT NOT NULL,
                    [Name] NVARCHAR(100) NOT NULL,
                    [Price] DECIMAL(10,2) NOT NULL,
                    [SortOrder] INT NOT NULL DEFAULT 0,
                    [IsActive] BIT NOT NULL DEFAULT 1,
                    CONSTRAINT [FK_ProductVariants_Products] FOREIGN KEY ([ProductId]) REFERENCES [dbo].[Products]([Id])
                );
            END
        ");
        Console.WriteLine("[Startup] ProductVariants table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] ProductVariants failed: {ex.Message}"); }

    // ── PointTransactions + User.PointsBalance + Order columns ─────────────
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Users' AND COLUMN_NAME = 'PointsBalance')
                ALTER TABLE [dbo].[Users] ADD [PointsBalance] INT NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Orders' AND COLUMN_NAME = 'PrescriptionImageUrl')
                ALTER TABLE [dbo].[Orders] ADD [PrescriptionImageUrl] NVARCHAR(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Orders' AND COLUMN_NAME = 'PointsEarned')
                ALTER TABLE [dbo].[Orders] ADD [PointsEarned] INT NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'OrderItems' AND COLUMN_NAME = 'VariantId')
                ALTER TABLE [dbo].[OrderItems] ADD [VariantId] INT NULL;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'OrderItems' AND COLUMN_NAME = 'VariantName')
                ALTER TABLE [dbo].[OrderItems] ADD [VariantName] NVARCHAR(100) NULL;

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'PointTransactions')
            BEGIN
                CREATE TABLE [dbo].[PointTransactions] (
                    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [UserId] INT NOT NULL,
                    [Amount] INT NOT NULL,
                    [Title] NVARCHAR(200) NOT NULL,
                    [Description] NVARCHAR(300) NULL,
                    [OrderId] INT NULL,
                    [CouponId] INT NULL,
                    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_PointTransactions_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users]([Id])
                );
            END
        ");
        Console.WriteLine("[Startup] Points schema ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Points schema failed: {ex.Message}"); }

    // ── ✅ الجديد: Create OtpCodes table (كود التحقق للتسجيل ونسيت كلمة المرور) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'OtpCodes')
            BEGIN
                CREATE TABLE [dbo].[OtpCodes] (
                    [Id]        INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Email]     NVARCHAR(150)  NOT NULL,
                    [Code]      NVARCHAR(10)   NOT NULL,
                    [Purpose]   NVARCHAR(30)   NOT NULL,
                    [IsUsed]    BIT            NOT NULL DEFAULT 0,
                    [ExpiresAt] DATETIME2      NOT NULL,
                    [CreatedAt] DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                CREATE INDEX [IX_OtpCodes_Email_Purpose] ON [dbo].[OtpCodes]([Email],[Purpose]);
            END
        ");
        Console.WriteLine("[Startup] OtpCodes table ready.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] OtpCodes table check failed: {ex.Message}");
    }

    // ── ✅ الجديد: Create DeliverySettings table (إعدادات سعر التوصيل القابلة للتعديل) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'DeliverySettings')
            BEGIN
                CREATE TABLE [dbo].[DeliverySettings] (
                    [Id]            INT           IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [FreeRadiusKm]  FLOAT         NOT NULL DEFAULT 3.0,
                    [ExtraFeePerKm] DECIMAL(10,2) NOT NULL DEFAULT 10.0,
                    [UpdatedAt]     DATETIME2     NOT NULL DEFAULT GETUTCDATE()
                );
                INSERT INTO [dbo].[DeliverySettings] ([FreeRadiusKm],[ExtraFeePerKm]) VALUES (3.0, 10.0);
            END
        ");
        Console.WriteLine("[Startup] DeliverySettings table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] DeliverySettings table check failed: {ex.Message}"); }

    // ── ✅ الجديد: Notifications.ActionUrl — توجيه الإشعار لمكان في التطبيق (زي البانرات) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Notifications' AND COLUMN_NAME = 'ActionUrl')
                ALTER TABLE [dbo].[Notifications] ADD [ActionUrl] NVARCHAR(300) NULL;
        ");
        Console.WriteLine("[Startup] Notifications.ActionUrl column ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] Notifications.ActionUrl check failed: {ex.Message}"); }

    // ── ✅ الجديد: AiSettings + SupportSessions/SupportMessages + Complaints ──
    // شات الدعم بالـ AI (اللي بيتحكم فيه الأدمن) + جدول الشكاوى.
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AiSettings')
            BEGIN
                CREATE TABLE [dbo].[AiSettings] (
                    [Id]           INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [IsEnabled]    BIT            NOT NULL DEFAULT 1,
                    [ApiKey]       NVARCHAR(300)  NULL,
                    [Model]        NVARCHAR(100)  NOT NULL DEFAULT 'openai/gpt-4o-mini',
                    [SystemPrompt] NVARCHAR(MAX)  NULL,
                    [MaxTokens]    INT            NOT NULL DEFAULT 512,
                    [UpdatedAt]    DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                INSERT INTO [dbo].[AiSettings] ([IsEnabled],[Model],[SystemPrompt],[MaxTokens])
                VALUES (1, 'openai/gpt-4o-mini',
                    N'أنت مساعد دعم عملاء ودود لتطبيق توصيل طلبات اسمه توصيلة. ساعد العميل في الاستفسارات
                    عن الطلبات والتتبع والإلغاء والاسترجاع. لو حسيت إن العميل بيشتكي من مشكلة حقيقية
                    (طلب متأخر جدًا، منتج فاسد أو ناقص، سلوك سيء من مندوب، مبلغ اتخصم غلط... إلخ)
                    استخدم أداة create_complaint عشان تسجل شكوى رسمية باسمه. ولو الموضوع معقد أو محتاج
                    قرار بشري أو العميل نفسه طلب يتكلم مع حد حقيقي، استخدم أداة escalate_to_admin.', 512);
            END
        ");

        // ✅ لو الجدول كان اتعمل قبل كده بموديل Anthropic القديم (claude-sonnet-...)
        // نرجّعه لموديل OpenRouter الافتراضي عشان النظام دلوقتي بيكلم OpenRouter مش Anthropic مباشرة
        await db.Database.ExecuteSqlRawAsync(@"
            IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AiSettings')
            BEGIN
                UPDATE [dbo].[AiSettings]
                SET [Model] = 'openai/gpt-4o-mini'
                WHERE [Model] LIKE 'claude-%';
            END
        ");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SupportSessions')
            BEGIN
                CREATE TABLE [dbo].[SupportSessions] (
                    [Id]            INT           IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [CustomerId]    INT           NOT NULL,
                    [Status]        NVARCHAR(20)  NOT NULL DEFAULT 'AI',
                    [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                    [LastMessageAt] DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_SupportSessions_Customer]
                        FOREIGN KEY ([CustomerId]) REFERENCES [Users]([Id])
                );
                CREATE INDEX [IX_SupportSessions_CustomerId] ON [dbo].[SupportSessions]([CustomerId]);
                CREATE INDEX [IX_SupportSessions_Status] ON [dbo].[SupportSessions]([Status]);
            END

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SupportMessages')
            BEGIN
                CREATE TABLE [dbo].[SupportMessages] (
                    [Id]         INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [SessionId]  INT            NOT NULL,
                    [SenderRole] NVARCHAR(20)   NOT NULL DEFAULT 'Customer',
                    [SenderId]   INT            NULL,
                    [Message]    NVARCHAR(2000) NOT NULL,
                    [CreatedAt]  DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_SupportMessages_Session]
                        FOREIGN KEY ([SessionId]) REFERENCES [SupportSessions]([Id]) ON DELETE CASCADE
                );
                CREATE INDEX [IX_SupportMessages_SessionId] ON [dbo].[SupportMessages]([SessionId]);
            END

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Complaints')
            BEGIN
                CREATE TABLE [dbo].[Complaints] (
                    [Id]                INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [CustomerId]        INT            NOT NULL,
                    [OrderId]           INT            NULL,
                    [SupportSessionId]  INT            NULL,
                    [Subject]           NVARCHAR(200)  NOT NULL,
                    [Description]       NVARCHAR(2000) NOT NULL,
                    [Status]            NVARCHAR(20)   NOT NULL DEFAULT 'Open',
                    [Source]            NVARCHAR(20)   NOT NULL DEFAULT 'Customer',
                    [AdminNote]         NVARCHAR(1000) NULL,
                    [CreatedAt]         DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
                    [ResolvedAt]        DATETIME2      NULL,
                    CONSTRAINT [FK_Complaints_Customer]
                        FOREIGN KEY ([CustomerId]) REFERENCES [Users]([Id]),
                    CONSTRAINT [FK_Complaints_Order]
                        FOREIGN KEY ([OrderId]) REFERENCES [Orders]([Id])
                );
                CREATE INDEX [IX_Complaints_CustomerId] ON [dbo].[Complaints]([CustomerId]);
                CREATE INDEX [IX_Complaints_Status] ON [dbo].[Complaints]([Status]);
            END
        ");
        Console.WriteLine("[Startup] AiSettings/SupportSessions/SupportMessages/Complaints tables ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] AI support/complaints tables check failed: {ex.Message}"); }

    // ── ✅ الجديد: Create SubscriptionPlans + RevenueSettlements tables (أرباحنا) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SubscriptionPlans')
            BEGIN
                CREATE TABLE [dbo].[SubscriptionPlans] (
                    [Id]           INT           IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [EntityType]   INT           NOT NULL,
                    [RestaurantId] INT           NULL,
                    [DriverId]     INT           NULL,
                    [Type]         INT           NOT NULL,
                    [Value]        DECIMAL(10,2) NOT NULL,
                    [IsActive]     BIT           NOT NULL DEFAULT 1,
                    [CreatedAt]    DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                    [UpdatedAt]    DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_SubscriptionPlans_Restaurants] FOREIGN KEY ([RestaurantId]) REFERENCES [Restaurants]([Id]),
                    CONSTRAINT [FK_SubscriptionPlans_Drivers] FOREIGN KEY ([DriverId]) REFERENCES [Drivers]([Id])
                );
                CREATE UNIQUE INDEX [UQ_SubscriptionPlans_Restaurant] ON [dbo].[SubscriptionPlans]([RestaurantId]) WHERE [RestaurantId] IS NOT NULL;
                CREATE UNIQUE INDEX [UQ_SubscriptionPlans_Driver] ON [dbo].[SubscriptionPlans]([DriverId]) WHERE [DriverId] IS NOT NULL;
            END
        ");
        Console.WriteLine("[Startup] SubscriptionPlans table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] SubscriptionPlans table check failed: {ex.Message}"); }

    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'RevenueSettlements')
            BEGIN
                CREATE TABLE [dbo].[RevenueSettlements] (
                    [Id]                 INT           IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [EntityType]         INT           NOT NULL,
                    [RestaurantId]       INT           NULL,
                    [DriverId]           INT           NULL,
                    [PeriodStart]        DATETIME2     NOT NULL,
                    [PeriodEnd]          DATETIME2     NOT NULL,
                    [OrdersTotal]        DECIMAL(10,2) NOT NULL DEFAULT 0,
                    [OrdersCount]        INT           NOT NULL DEFAULT 0,
                    [PlanType]           INT           NOT NULL,
                    [PlanValue]          DECIMAL(10,2) NOT NULL,
                    [AmountDue]          DECIMAL(10,2) NOT NULL DEFAULT 0,
                    [AmountPaid]         DECIMAL(10,2) NOT NULL DEFAULT 0,
                    [Status]             INT           NOT NULL DEFAULT 0,
                    [CollectedByAdminId] INT           NULL,
                    [PaidAt]             DATETIME2     NULL,
                    [Notes]              NVARCHAR(500) NULL,
                    [CreatedAt]          DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_RevenueSettlements_Restaurants] FOREIGN KEY ([RestaurantId]) REFERENCES [Restaurants]([Id]),
                    CONSTRAINT [FK_RevenueSettlements_Drivers] FOREIGN KEY ([DriverId]) REFERENCES [Drivers]([Id])
                );
                CREATE INDEX [IX_RevenueSettlements_Restaurant] ON [dbo].[RevenueSettlements]([RestaurantId]);
                CREATE INDEX [IX_RevenueSettlements_Driver] ON [dbo].[RevenueSettlements]([DriverId]);
                CREATE INDEX [IX_RevenueSettlements_Status] ON [dbo].[RevenueSettlements]([Status]);
                CREATE INDEX [IX_RevenueSettlements_Period] ON [dbo].[RevenueSettlements]([PeriodStart],[PeriodEnd]);
            END
        ");
        Console.WriteLine("[Startup] RevenueSettlements table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] RevenueSettlements table check failed: {ex.Message}"); }

    // ── Public website and social links ───────────────────────────────────────
    try
    {
        // خطوة 1: إنشاء الجدول لو مش موجود (ده مكانش بيحصل غير أول مرة)
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SiteLinks')
            BEGIN
                CREATE TABLE [dbo].[SiteLinks] (
                    [Id]        INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [Key]       NVARCHAR(50)   NOT NULL,
                    [Title]     NVARCHAR(100)  NOT NULL,
                    [Url]       NVARCHAR(500)  NOT NULL,
                    [Icon]      NVARCHAR(100)  NULL,
                    [IsActive]  BIT            NOT NULL DEFAULT 1,
                    [SortOrder] INT            NOT NULL DEFAULT 0,
                    [UpdatedAt] DATETIME2      NOT NULL DEFAULT GETUTCDATE()
                );
                CREATE UNIQUE INDEX [UQ_SiteLinks_Key] ON [dbo].[SiteLinks]([Key]);
            END
        ");

        // خطوة 2: تأكيد وجود الصفوف الافتراضية دايمًا، حتى لو الجدول كان موجود
        // من قبل من غير بيانات (ده كان سبب رجوع اللينكات فاضية: الـ INSERT
        // القديم كان جوه نفس الـ IF NOT EXISTS بتاعة إنشاء الجدول، فلو الجدول
        // كان موجود بالفعل (فاضي) الـ seed مكانش بيتنفذ خالص).
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM [dbo].[SiteLinks] WHERE [Key] = N'website')
                INSERT INTO [dbo].[SiteLinks] ([Key],[Title],[Url],[Icon],[IsActive],[SortOrder])
                VALUES (N'website', N'Website', N'https://Taly-app.com', N'web', 1, 1);

            IF NOT EXISTS (SELECT 1 FROM [dbo].[SiteLinks] WHERE [Key] = N'facebook')
                INSERT INTO [dbo].[SiteLinks] ([Key],[Title],[Url],[Icon],[IsActive],[SortOrder])
                VALUES (N'facebook', N'Facebook', N'https://facebook.com/Taly', N'facebook', 1, 2);

            IF NOT EXISTS (SELECT 1 FROM [dbo].[SiteLinks] WHERE [Key] = N'instagram')
                INSERT INTO [dbo].[SiteLinks] ([Key],[Title],[Url],[Icon],[IsActive],[SortOrder])
                VALUES (N'instagram', N'Instagram', N'https://instagram.com/Taly', N'instagram', 1, 3);

            IF NOT EXISTS (SELECT 1 FROM [dbo].[SiteLinks] WHERE [Key] = N'x')
                INSERT INTO [dbo].[SiteLinks] ([Key],[Title],[Url],[Icon],[IsActive],[SortOrder])
                VALUES (N'x', N'X', N'https://x.com/Taly', N'x', 1, 4);

            IF NOT EXISTS (SELECT 1 FROM [dbo].[SiteLinks] WHERE [Key] = N'tiktok')
                INSERT INTO [dbo].[SiteLinks] ([Key],[Title],[Url],[Icon],[IsActive],[SortOrder])
                VALUES (N'tiktok', N'TikTok', N'https://www.tiktok.com/@Taly', N'tiktok', 1, 5);
        ");
        Console.WriteLine("[Startup] SiteLinks table ready.");
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] SiteLinks table check failed: {ex.Message}"); }
}

app.UseSwagger();
app.UseSwaggerUI();

// Global exception handler - returns JSON with message instead of HTML 500 page
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/json";
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var msg = feature?.Error?.InnerException?.Message ?? feature?.Error?.Message ?? "Unexpected error";
    await ctx.Response.WriteAsJsonAsync(new { message = $"Server error: {msg}" });
}));

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TrackingHub>("/hubs/tracking");

app.Run();