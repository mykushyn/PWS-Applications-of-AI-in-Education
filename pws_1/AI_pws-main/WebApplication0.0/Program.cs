var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// ✅ Serve index.html automatically
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// ✅ (Optional) if you want /Intro still accessible:
app.MapRazorPages();

// ✅ Serve your SignalR Hub for AI backend
app.MapHub<ChatHub>("/chathub");

// ✅ This must be last
app.Run();
