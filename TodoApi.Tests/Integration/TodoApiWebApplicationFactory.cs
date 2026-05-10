using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TodoApi.Tests.Integration;

public class TodoApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _externalBaseAddress;
    private readonly string _databaseName = $"todo-{Guid.NewGuid()}";
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    public TodoApiWebApplicationFactory(string externalBaseAddress)
    {
        _externalBaseAddress = externalBaseAddress;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(
            (_, cfg) =>
                cfg.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ExternalApi:BaseAddress"] = _externalBaseAddress,
                        ["Sync:Enabled"] = "false",
                        ["ConnectionStrings:TodoContext"] = "Server=ignored;",
                    }
                )
        );

        builder.ConfigureServices(services =>
        {
            RemoveAll(services, typeof(DbContextOptions<TodoContext>));
            RemoveAll(services, typeof(DbContextOptions));
            RemoveAll(services, typeof(TodoContext));

            services.AddDbContext<TodoContext>(o =>
                o.UseInMemoryDatabase(_databaseName, _databaseRoot)
            );
        });
    }

    private static void RemoveAll(IServiceCollection services, Type serviceType)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == serviceType)
            {
                services.RemoveAt(i);
            }
        }
    }

    public IServiceScope CreateScope() => Services.CreateScope();
}
