using FluentValidation.TestHelper;
using TodoApi.Dtos;
using TodoApi.Validators;

namespace TodoApi.Tests.Validators;

public class CreateTodoListItemValidatorTests
{
    private readonly CreateTodoListItemValidator _validator = new();

    [Fact]
    public void Should_have_error_when_Description_is_null()
    {
        var model = new CreateTodoListItem { Description = null! };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Should_have_error_when_Description_is_empty()
    {
        var model = new CreateTodoListItem { Description = string.Empty };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Should_not_have_error_when_Description_is_provided()
    {
        var model = new CreateTodoListItem { Description = "Buy milk" };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }
}
