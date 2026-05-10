using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SharpGrip.FluentValidation.AutoValidation.Mvc.Extensions;
using TodoApi.Configuration;
using TodoApi.Hosting;
using TodoApi.Hubs;
using TodoApi.Services;
using TodoApi.Sync.DependencyInjection;

const string FrontendCorsPolicy = "FrontendCorsPolicy";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<TodoContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("TodoContext"))
);

builder.Services.AddScoped<TodoApi.Sync.Data.ISyncDbContext>(sp =>
    sp.GetRequiredService<TodoContext>()
);

builder.Services.AddTodoSync(builder.Configuration);

builder
    .Services.AddOptions<RealtimeOptions>()
    .Bind(builder.Configuration.GetSection("Realtime"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSignalR();
builder.Services.AddSingleton<IOutboxBroadcaster, OutboxBroadcaster>();
builder.Services.AddHostedService<OutboxBroadcastService>();

// CORS — SignalR with credentials requires explicit origins (cannot use *).
var allowedOrigins =
    builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        FrontendCorsPolicy,
        policy =>
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials()
    );
});

builder
    .Services.AddScoped<ITodoListService, TodoListService>()
    .AddScoped<ITodoListItemService, TodoListItemService>()
    .AddScoped<ISyncStatusService, SyncStatusService>()
    .AddEndpointsApiExplorer()
    .AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "TodoApi", Version = "v1" });
    })
    .AddControllers();

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "TodoApi v1");
    });

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TodoContext>();
    db.Database.Migrate();
}

// CORS must come before MapHub / MapControllers so the hub negotiate request gets the headers.
app.UseCors(FrontendCorsPolicy);

app.UseAuthorization();
app.MapControllers();
app.MapHub<TodoSyncHub>("/hubs/todosync");
app.Run();

public partial class Program { }
