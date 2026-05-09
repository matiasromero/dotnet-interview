using FluentValidation;
using TodoApi.Dtos;

namespace TodoApi.Validators;

public class UpdateTodoListItemValidator : AbstractValidator<UpdateTodoListItem>
{
    public UpdateTodoListItemValidator()
    {
        RuleFor(x => x.Description).NotEmpty();
    }
}
