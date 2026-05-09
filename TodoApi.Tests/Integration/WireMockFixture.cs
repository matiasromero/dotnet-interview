using WireMock.Server;

namespace TodoApi.Tests.Integration;

public sealed class WireMockFixture : IDisposable
{
    public WireMockServer Server { get; }

    public WireMockFixture()
    {
        Server = WireMockServer.Start();
    }

    public string Url => Server.Url ?? throw new InvalidOperationException("WireMock not started");

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
    }
}
