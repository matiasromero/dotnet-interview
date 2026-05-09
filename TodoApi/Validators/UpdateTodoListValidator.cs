using FluentValidation;
using TodoApi.Dtos;

namespace TodoApi.Validators;

public class UpdateTodoListValidator : AbstractValidator<UpdateTodoList>
{
    public UpdateTodoListValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
    }
}
