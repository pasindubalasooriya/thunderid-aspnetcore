using ThunderID.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThunderIDApi(options =>
{
    options.Authority = builder.Configuration["ThunderID:Authority"]!;
    options.Audience = builder.Configuration["ThunderID:Audience"]!;
});

builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy("billing:read", "billing:read");

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/public", () => Results.Ok(new { message = "Anyone can read this." }));

app.MapGet("/me", (HttpContext context) => Results.Ok(new
{
    claims = context.User.Claims.Select(c => new { c.Type, c.Value }),
})).RequireAuthorization();

app.MapGet("/billing", () => Results.Ok(new
{
    invoices = new[]
    {
        new { id = "INV-1001", amount = 120.00m, status = "paid" },
        new { id = "INV-1002", amount = 75.50m, status = "due" },
    },
})).RequireAuthorization("billing:read");

app.Run();
