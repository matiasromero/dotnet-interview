using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TodoApi.Tests.Integration;

public sealed class TodoApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _externalBaseAddress;
    private readonly string _databaseName = $"todo-{Guid.NewGuid()}";

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
            var dbDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<TodoContext>)
            );
            if (dbDescriptor is not null)
            {
                services.Remove(dbDescriptor);
            }

            services.AddDbContext<TodoContext>(o => o.UseInMemoryDatabase(_databaseName));
        });
    }

    public IServiceScope CreateScope() => Services.CreateScope();
}
