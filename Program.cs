using CheckScam.Models;
using CheckScam.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddDbContext<CheckScamDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<CheckScamDbContext>()
.AddDefaultTokenProviders();
builder.Services.AddScoped<PhoneCheckService>();
builder.Services.AddHttpClient();
builder.Services.AddControllersWithViews(); // Chỉ định một lần

// Configure application cookie
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Gr1/Login";
    options.AccessDeniedPath = "/Gr1/Index";
});

// Enable static files for media folder
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Gr1/Error"); // Thay bằng Gr1/Error để khớp controller
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Serve media folder
app.UseFileServer(new FileServerOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "media")),
    RequestPath = "/media",
    EnableDirectoryBrowsing = true
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CheckScam.Middleware.AdminRequiredMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Gr1}/{action=Index}/{id?}"); // Đặt Gr1 làm mặc định

// Seed dữ liệu và tạo role Superuser
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CheckScamDbContext>();
    if (!context.ScamPosts.Any())
    {
        context.SaveChanges();
    }

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roleManager.RoleExistsAsync("Superuser"))
    {
        await roleManager.CreateAsync(new IdentityRole("Superuser"));
    }
}

app.Run();