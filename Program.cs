

// app.Run();
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NCBA.DCL.Data;
using System.Data;
using NCBA.DCL.Helpers;
using NCBA.DCL.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Allow flexible property naming: PascalCase, camelCase, snake_case all work
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = new FlexibleNamingPolicy();
        options.JsonSerializerOptions.WriteIndented = false;
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// Configure Database (MySQL with Pomelo)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
ServerVersion serverVersion;
try
{
    serverVersion = ServerVersion.AutoDetect(connectionString);
}
catch (Exception)
{
    // Fallback for design-time tools or if DB is unreachable
    serverVersion = new MySqlServerVersion(new Version(8, 0, 0));
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

// Configure JWT Authentication
var jwtSecret = builder.Configuration["JwtSettings:Secret"]
    ?? throw new InvalidOperationException("JWT Secret not configured");
var jwtIssuer = builder.Configuration["JwtSettings:Issuer"];
var jwtAudience = builder.Configuration["JwtSettings:Audience"];

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
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };
});

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();

// Register SignalR and online user tracker
builder.Services.AddSignalR();
builder.Services.AddSingleton<OnlineUserTracker>();

// Register custom services
builder.Services.AddScoped<JwtTokenGenerator>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IMFAService, MFAService>();
builder.Services.AddScoped<ISSOService, SSOService>();
builder.Services.Configure<ChatbotOptions>(builder.Configuration.GetSection("Chatbot"));
builder.Services.AddSingleton<IChatbotService, ChatbotService>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("ChatbotProxy")
    .ConfigureHttpClient((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptionsMonitor<ChatbotOptions>>().CurrentValue;
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length > 0)
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
            return;
        }

        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NCBA DCL API", Version = "v1" });

    // Add JWT authentication to Swagger
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable CORS
app.UseCors();

// Serve static files from uploads folder
app.UseStaticFiles();
var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
if (!Directory.Exists(uploadsPath))
{
    Directory.CreateDirectory(uploadsPath);
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<ApplicationDbContext>();
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        // Ensure the Extensions.LoanAmount column exists in the database.
        try
        {
            var conn = context.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            // Ensure the Extensions.LoanAmount column exists
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Extensions' AND COLUMN_NAME = 'LoanAmount'";
                var countObj = await cmd.ExecuteScalarAsync();
                var count = Convert.ToInt32(countObj);
                if (count == 0)
                {
                    logger.LogInformation("Extensions.LoanAmount column missing; adding column now.");
                    using var alter = conn.CreateCommand();
                    alter.CommandText = "ALTER TABLE `Extensions` ADD COLUMN `LoanAmount` decimal(65,30) NULL";
                    await alter.ExecuteNonQueryAsync();
                    logger.LogInformation("Extensions.LoanAmount column added.");
                }
            }

            // Ensure the Extensions.SelectedDocumentsJson column exists in the database.
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Extensions' AND COLUMN_NAME = 'SelectedDocumentsJson'";
                var countObj2 = await cmd2.ExecuteScalarAsync();
                var count2 = Convert.ToInt32(countObj2);
                if (count2 == 0)
                {
                    logger.LogInformation("Extensions.SelectedDocumentsJson column missing; adding column now.");
                    using var alter2 = conn.CreateCommand();
                    alter2.CommandText = "ALTER TABLE `Extensions` ADD COLUMN `SelectedDocumentsJson` TEXT NULL";
                    await alter2.ExecuteNonQueryAsync();
                    logger.LogInformation("Extensions.SelectedDocumentsJson column added.");
                }
            }

            if (conn.State == ConnectionState.Open)
            {
                await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure Extensions schema columns exist");
        }

        await DbInitializer.SeedData(context);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred seeding the DB.");
    }
}

app.MapControllers();

// Map SignalR hub
app.MapHub<NCBA.DCL.Hubs.DclHub>("/hub/dcl");

// Health check endpoint
app.MapGet("/", () => new { message = "NCBA DCL API is running", version = "1.0.0" });

Console.WriteLine($"✅ Server running on port {builder.Configuration["PORT"] ?? "5000"}");

app.Run();