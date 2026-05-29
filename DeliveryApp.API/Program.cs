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

// ?????????????????????????????????????????????????????????????
// BUG FIX B: ???? ChatMessages ?? ????? ?? ?????????
// ???? ????? ??? DbContext ???? migration
// ????: ???? CREATE TABLE IF NOT EXISTS ??? startup
// ?????????????????????????????????????????????????????????????
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
        // log only ? don't crash the app
        Console.WriteLine($"[Startup] ChatMessages table check failed: {ex.Message}");
    }
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
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TrackingHub>("/hubs/tracking");

app.Run();