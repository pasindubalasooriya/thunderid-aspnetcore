using ThunderID.AspNetCore;
using ThunderID.AspNetCore.BackchannelLogout;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

builder.Services.AddThunderID(options =>
{
    options.Authority = builder.Configuration["ThunderID:Authority"]!;
    options.ClientId = builder.Configuration["ThunderID:ClientId"]!;
    options.ClientSecret = builder.Configuration["ThunderID:ClientSecret"];
    options.Scopes.Add("billing:read");
    options.Resource = builder.Configuration["BillingApi:BaseUrl"];
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapThunderIDBackchannelLogout("/backchannel-logout");

app.Run();
