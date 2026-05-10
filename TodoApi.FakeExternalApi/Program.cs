using System.Text.Json;
using Microsoft.OpenApi.Models;
using TodoApi.FakeExternalApi.Endpoints;
using TodoApi.FakeExternalApi.Middleware;
using TodoApi.FakeExternalApi.State;

namespace TodoApi.FakeExternalApi;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        builder.Services.AddSingleton<FakeStore>();
        builder.Services.AddSingleton<ChaosState>();

        builder
            .Services.AddEndpointsApiExplorer()
            .AddSwaggerGen(options =>
            {
                options.SwaggerDoc(
                    "v1",
                    new OpenApiInfo { Title = "TodoApi.FakeExternalApi", Version = "v1" }
                );
            });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "TodoApi.FakeExternalApi v1");
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<ChaosMiddleware>();

        app.MapTodoListEndpoints();
        app.MapTodoListItemEndpoints();
        app.MapAdminEndpoints();

        app.Run();
    }
}
