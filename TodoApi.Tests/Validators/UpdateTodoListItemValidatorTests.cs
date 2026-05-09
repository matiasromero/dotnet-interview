using FluentValidation.TestHelper;
using TodoApi.Dtos;
using TodoApi.Validators;

namespace TodoApi.Tests.Validators;

public class UpdateTodoListItemValidatorTests
{
    private readonly UpdateTodoListItemValidator _validator = new();

    [Fact]
    public void Should_have_error_when_Description_is_null()
    {
        var model = new UpdateTodoListItem { Description = null!, IsCompleted = false };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Should_have_error_when_Description_is_empty()
    {
        var model = new UpdateTodoListItem { Description = string.Empty, IsCompleted = false };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Should_not_have_error_when_Description_is_provided()
    {
        var model = new UpdateTodoListItem { Description = "Updated", IsCompleted = true };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }
}
