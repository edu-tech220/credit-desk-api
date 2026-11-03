namespace CreditDesk.Api.Domain;

/// <summary>Base de los errores de negocio; el manejador global los traduce a ProblemDetails.</summary>
public abstract class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>La operación no es válida en el estado actual del recurso (409).</summary>
public sealed class ConflictException(string code, string message) : DomainException(code, message);

/// <summary>El usuario está autenticado pero no puede operar sobre el recurso (403).</summary>
public sealed class ForbiddenException(string code, string message) : DomainException(code, message);

/// <summary>El recurso no existe o no es visible para el usuario (404).</summary>
public sealed class NotFoundException(string message) : DomainException("not_found", message);

/// <summary>La versión enviada en If-Match ya no es la vigente (412).</summary>
public sealed class PreconditionFailedException(string message) : DomainException("precondition_failed", message);

/// <summary>Una o más reglas de negocio no se cumplen (422).</summary>
public sealed class BusinessRuleException : DomainException
{
    public BusinessRuleException(IDictionary<string, string[]> errors)
        : base("business_rules_violated", "La solicitud no cumple las reglas de negocio.")
    {
        Errors = new Dictionary<string, string[]>(errors);
    }

    public BusinessRuleException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
