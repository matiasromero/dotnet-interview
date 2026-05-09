using FluentValidation;
using TodoApi.Dtos;

namespace TodoApi.Validators;

public class CreateTodoListValidator : AbstractValidator<CreateTodoList>
{
    public CreateTodoListValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
    }
}
