using FluentValidation.TestHelper;
using TodoApi.Dtos;
using TodoApi.Validators;

namespace TodoApi.Tests.Validators;

public class UpdateTodoListValidatorTests
{
    private readonly UpdateTodoListValidator _validator = new();

    [Fact]
    public void Should_have_error_when_Name_is_null()
    {
        var model = new UpdateTodoList { Name = null! };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Should_have_error_when_Name_is_empty()
    {
        var model = new UpdateTodoList { Name = string.Empty };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Should_not_have_error_when_Name_is_provided()
    {
        var model = new UpdateTodoList { Name = "Renamed" };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }
}
