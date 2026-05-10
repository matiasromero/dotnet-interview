namespace TodoApi.FakeExternalApi.Models;

public sealed class SeedRequest
{
    public List<ExternalTodoList> Lists { get; set; } = new();
}
