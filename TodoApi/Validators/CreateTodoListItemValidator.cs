using FluentValidation;
using TodoApi.Dtos;

namespace TodoApi.Validators;

public class CreateTodoListItemValidator : AbstractValidator<CreateTodoListItem>
{
    public CreateTodoListItemValidator()
    {
        RuleFor(x => x.Description).NotEmpty();
    }
}
