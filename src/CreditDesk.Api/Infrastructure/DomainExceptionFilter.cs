using CreditDesk.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CreditDesk.Api.Infrastructure;

/// <summary>
/// Traduce las excepciones de dominio a respuestas RFC 7807 con un <c>code</c> estable para el cliente.
/// Es un filtro MVC (y no un IExceptionHandler) para que los errores de negocio esperados
/// no se registren como fallas del servidor; lo inesperado sigue llegando al manejador global (500).
/// </summary>
public sealed class DomainExceptionFilter(ProblemDetailsFactory factory) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DomainException domain)
            return;

        var (status, title) = domain switch
        {
            BusinessRuleException => (StatusCodes.Status422UnprocessableEntity, "Reglas de negocio no cumplidas"),
            NotFoundException => (StatusCodes.Status404NotFound, "No encontrado"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Operación no permitida"),
            PreconditionFailedException => (StatusCodes.Status412PreconditionFailed, "Versión desactualizada"),
            _ => (StatusCodes.Status409Conflict, "Conflicto de estado"),
        };

        ProblemDetails problem;
        if (domain is BusinessRuleException rules)
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, errors) in rules.Errors)
                foreach (var error in errors)
                    modelState.AddModelError(field, error);
            problem = factory.CreateValidationProblemDetails(context.HttpContext, modelState, status, title, detail: domain.Message);
        }
        else
        {
            problem = factory.CreateProblemDetails(context.HttpContext, status, title, detail: domain.Message);
        }

        problem.Extensions["code"] = domain.Code;
        context.Result = new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
