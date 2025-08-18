
using GetlinkFshare.Services;
using GetlinkFshare.SignalRHub;
using GetlinkFshare.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

namespace GetlinkFshare
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var builder = WebApplication.CreateBuilder(args);

            //ĐÃ THÊM: Cấu hình Forwarded Headers dùng cho Linux reverse proxy
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            });

            // Đăng ký dịch vụ
            builder.Services.AddSingleton<PuppeteerService>();
            builder.Services.AddControllers();
            builder.Services.AddMemoryCache();

            // *** ĐÃ THÊM: Đăng ký SignalR và Background Worker ***
            builder.Services.AddSignalR();
            builder.Services.AddHostedService<FshareInfoWorker>();

            //Loại bỏ HttpClient mặc định để tránh lỗi khi sử dụng PuppeteerSharp
            //builder.Services.AddHttpClient();

            // Cấu hình JWT Authentication
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
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];

                        // Nếu request đang đi đến Hub của chúng ta...
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) &&
                            (path.StartsWithSegments("/fshareInfoHub")))
                        {
                            // Đọc token từ query string
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            // Cấu hình Swagger/OpenAPI
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Puppeteer API Demo", Version = "v1" });
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "Bearer"
                });
                c.AddSecurityRequirement(new OpenApiSecurityRequirement()
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        },
                        Scheme = "oauth2",
                        Name = "Bearer",
                        In = ParameterLocation.Header,
                    },
                    new List<string>()
                }
            });
            });

            var app = builder.Build();

            // *** ĐÃ THÊM: Sử dụng middleware Forwarded Headers để Sử dụng cho reverse proxy trên Linux ***
            // Phải đặt trước các middleware khác như HttpsRedirection, Auth...
            app.UseForwardedHeaders();

            var puppeteerService = app.Services.GetRequiredService<PuppeteerService>();


            // ĐÃ THÊM: Đăng ký sự kiện để dọn dẹp Puppeteer khi ứng dụng kết thúc
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => { puppeteerService.DisposeAsync().AsTask().Wait(); };
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; puppeteerService.DisposeAsync().AsTask().Wait(); Environment.Exit(0); };

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // Không cần CORS khi deploy chung
            // app.UseCors(MyAllowSpecificOrigins);

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            // *** ĐÃ THÊM: Map endpoint cho SignalR Hub ***
            app.MapHub<FshareInfoHub>("/fshareInfoHub");

            app.Run();
        }
    }
}
