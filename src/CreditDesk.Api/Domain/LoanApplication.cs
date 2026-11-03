using System.Text.RegularExpressions;

namespace CreditDesk.Api.Domain;

/// <summary>
/// Agregado raíz de una solicitud de crédito. Todas las reglas de estado viven aquí:
/// los controladores y servicios sólo orquestan, nunca cambian <see cref="Status"/> directamente.
/// </summary>
public partial class LoanApplication
{
    public const int MaxGuarantors = 2;

    private static readonly IReadOnlyDictionary<ApplicationStatus, ApplicationStatus[]> AllowedTransitions =
        new Dictionary<ApplicationStatus, ApplicationStatus[]>
        {
            [ApplicationStatus.Draft] = [ApplicationStatus.Submitted, ApplicationStatus.Cancelled],
            [ApplicationStatus.Submitted] = [ApplicationStatus.InReview, ApplicationStatus.Cancelled],
            [ApplicationStatus.InReview] = [ApplicationStatus.Approved, ApplicationStatus.Rejected],
            [ApplicationStatus.Approved] = [ApplicationStatus.Disbursed],
        };

    private LoanApplication()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ApplicantId { get; private set; }
    public int ProductId { get; private set; }
    public LoanProduct Product { get; private set; } = null!;
    public decimal Amount { get; private set; }
    public int TermMonths { get; private set; }
    public decimal MonthlyIncome { get; private set; }
    public ApplicationStatus Status { get; private set; }

    /// <summary>
    /// Columna persistida para el índice único filtrado "una solicitud abierta por solicitante y producto".
    /// </summary>
    public bool IsOpen { get; private set; } = true;

    public Guid? AssignedAnalystId { get; private set; }
    public string? DecisionNote { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? DecidedAt { get; private set; }

    /// <summary>Token de concurrencia optimista; cambia en cada modificación y se expone como ETag.</summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    public List<Guarantor> Guarantors { get; private set; } = [];
    public List<ApplicationDocument> Documents { get; private set; } = [];
    public List<StatusChange> History { get; private set; } = [];

    public static bool IsTerminal(ApplicationStatus status) => !AllowedTransitions.ContainsKey(status);

    public static LoanApplication Create(Guid applicantId, LoanProduct product, decimal amount, int termMonths,
        decimal monthlyIncome, DateTime now)
    {
        if (!product.IsActive)
            throw new ConflictException("product_inactive", "El producto no está disponible.");

        ThrowIfInvalidTerms(product, amount, termMonths, monthlyIncome);

        var application = new LoanApplication
        {
            ApplicantId = applicantId,
            ProductId = product.Id,
            Product = product,
            Amount = amount,
            TermMonths = termMonths,
            MonthlyIncome = monthlyIncome,
            Status = ApplicationStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        application.History.Add(new StatusChange(null, ApplicationStatus.Draft, applicantId, now, null));
        return application;
    }

    public decimal MonthlyPayment => Amortization.MonthlyPayment(Amount, Product.AnnualInterestRate, TermMonths);

    public void UpdateTerms(Guid userId, decimal amount, int termMonths, decimal monthlyIncome, DateTime now)
    {
        EnsureEditableDraft(userId);
        ThrowIfInvalidTerms(Product, amount, termMonths, monthlyIncome);

        Amount = amount;
        TermMonths = termMonths;
        MonthlyIncome = monthlyIncome;
        Touch(now);
    }

    public Guarantor AddGuarantor(Guid userId, string fullName, string rfc, decimal monthlyIncome, DateTime now)
    {
        EnsureEditableDraft(userId);

        var normalizedRfc = rfc.Trim().ToUpperInvariant();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(fullName))
            errors["fullName"] = ["El nombre del aval es obligatorio."];
        if (!RfcRegex().IsMatch(normalizedRfc))
            errors["rfc"] = ["El RFC no tiene un formato válido."];
        if (monthlyIncome <= 0)
            errors["monthlyIncome"] = ["El ingreso del aval debe ser mayor a cero."];
        if (errors.Count > 0)
            throw new BusinessRuleException(errors);

        if (Guarantors.Count >= MaxGuarantors)
            throw new ConflictException("guarantor_limit", $"Una solicitud admite como máximo {MaxGuarantors} avales.");
        if (Guarantors.Any(g => g.Rfc == normalizedRfc))
            throw new ConflictException("guarantor_duplicated", "Ese aval ya está registrado en la solicitud.");

        var guarantor = new Guarantor(fullName.Trim(), normalizedRfc, monthlyIncome);
        Guarantors.Add(guarantor);
        Touch(now);
        return guarantor;
    }

    public void RemoveGuarantor(Guid userId, Guid guarantorId, DateTime now)
    {
        EnsureEditableDraft(userId);
        var guarantor = Guarantors.FirstOrDefault(g => g.Id == guarantorId)
            ?? throw new NotFoundException("El aval no existe en esta solicitud.");
        Guarantors.Remove(guarantor);
        Touch(now);
    }

    /// <summary>Registra (o reemplaza) el documento de un tipo dentro del expediente.</summary>
    public ApplicationDocument AttachDocument(Guid userId, DocumentType type, string fileName, DateTime now)
    {
        EnsureEditableDraft(userId);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new BusinessRuleException("fileName", "El nombre del archivo es obligatorio.");

        Documents.RemoveAll(d => d.Type == type);
        var document = new ApplicationDocument(type, fileName.Trim(), now);
        Documents.Add(document);
        Touch(now);
        return document;
    }

    /// <summary>
    /// Envía la solicitud a revisión. Valida todas las reglas a la vez para que el solicitante
    /// vea en una sola respuesta todo lo que le falta corregir.
    /// </summary>
    public void Submit(Guid userId, DateTime now)
    {
        EnsureOwner(userId);
        EnsureCanTransitionTo(ApplicationStatus.Submitted);

        var errors = new Dictionary<string, string[]>();
        if (!Product.IsActive)
            errors["productCode"] = ["El producto ya no está disponible."];

        Product.CollectTermsErrors(Amount, TermMonths, MonthlyIncome, errors);

        if (errors.Count == 0)
        {
            var payment = MonthlyPayment;
            var maxPayment = Math.Round(MonthlyIncome * Product.MaxPaymentToIncomeRatio, 2);
            if (payment > maxPayment)
                errors["monthlyIncome"] =
                [$"La mensualidad ({payment:N2}) supera el {Product.MaxPaymentToIncomeRatio:P0} del ingreso ({maxPayment:N2})."];

            if (Product.RequiresGuarantor(Amount))
            {
                if (Guarantors.Count == 0)
                    errors["guarantors"] = [$"Montos mayores a {Product.GuarantorThreshold:N2} requieren al menos un aval."];
                else if (Guarantors.Sum(g => g.MonthlyIncome) < payment)
                    errors["guarantors"] = ["El ingreso conjunto de los avales debe cubrir al menos una mensualidad."];
            }
        }

        var missing = Product.RequiredDocuments.Except(Documents.Select(d => d.Type)).ToArray();
        if (missing.Length > 0)
            errors["documents"] = [$"Faltan documentos: {string.Join(", ", missing)}."];

        if (errors.Count > 0)
            throw new BusinessRuleException(errors);

        SubmittedAt = now;
        TransitionTo(ApplicationStatus.Submitted, userId, now, null);
    }

    public void StartReview(Guid analystId, DateTime now)
    {
        EnsureCanTransitionTo(ApplicationStatus.InReview);
        AssignedAnalystId = analystId;
        TransitionTo(ApplicationStatus.InReview, analystId, now, null);
    }

    public void Approve(Guid analystId, string? note, DateTime now)
    {
        EnsureAssignedAnalyst(analystId);
        EnsureCanTransitionTo(ApplicationStatus.Approved);
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        DecidedAt = now;
        TransitionTo(ApplicationStatus.Approved, analystId, now, DecisionNote);
    }

    public void Reject(Guid analystId, string reason, DateTime now)
    {
        EnsureAssignedAnalyst(analystId);
        EnsureCanTransitionTo(ApplicationStatus.Rejected);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new BusinessRuleException("reason", "El motivo de rechazo debe tener al menos 10 caracteres.");

        DecisionNote = reason.Trim();
        DecidedAt = now;
        TransitionTo(ApplicationStatus.Rejected, analystId, now, DecisionNote);
    }

    public void Disburse(Guid analystId, DateTime now)
    {
        EnsureAssignedAnalyst(analystId);
        EnsureCanTransitionTo(ApplicationStatus.Disbursed);
        TransitionTo(ApplicationStatus.Disbursed, analystId, now, null);
    }

    public void Cancel(Guid userId, DateTime now)
    {
        EnsureOwner(userId);
        EnsureCanTransitionTo(ApplicationStatus.Cancelled);
        TransitionTo(ApplicationStatus.Cancelled, userId, now, null);
    }

    private static void ThrowIfInvalidTerms(LoanProduct product, decimal amount, int termMonths, decimal monthlyIncome)
    {
        var errors = new Dictionary<string, string[]>();
        product.CollectTermsErrors(amount, termMonths, monthlyIncome, errors);
        if (errors.Count > 0)
            throw new BusinessRuleException(errors);
    }

    private void EnsureOwner(Guid userId)
    {
        if (userId != ApplicantId)
            throw new ForbiddenException("not_owner", "Sólo el solicitante puede realizar esta acción.");
    }

    private void EnsureEditableDraft(Guid userId)
    {
        EnsureOwner(userId);
        if (Status != ApplicationStatus.Draft)
            throw new ConflictException("not_editable", "Sólo se pueden modificar solicitudes en borrador.");
    }

    private void EnsureAssignedAnalyst(Guid analystId)
    {
        if (AssignedAnalystId is not null && AssignedAnalystId != analystId)
            throw new ForbiddenException("not_assigned", "La solicitud está asignada a otro analista.");
    }

    private void EnsureCanTransitionTo(ApplicationStatus target)
    {
        if (!AllowedTransitions.TryGetValue(Status, out var targets) || !targets.Contains(target))
            throw new ConflictException("invalid_transition", $"No se puede pasar de {Status} a {target}.");
    }

    private void TransitionTo(ApplicationStatus target, Guid actorId, DateTime now, string? note)
    {
        History.Add(new StatusChange(Status, target, actorId, now, note));
        Status = target;
        IsOpen = target is not (ApplicationStatus.Rejected or ApplicationStatus.Cancelled or ApplicationStatus.Disbursed);
        Touch(now);
    }

    private void Touch(DateTime now)
    {
        UpdatedAt = now;
        Version = Guid.NewGuid();
    }

    [GeneratedRegex("^[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}$")]
    private static partial Regex RfcRegex();
}

public class Guarantor
{
    private Guarantor()
    {
    }

    public Guarantor(string fullName, string rfc, decimal monthlyIncome)
    {
        FullName = fullName;
        Rfc = rfc;
        MonthlyIncome = monthlyIncome;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid LoanApplicationId { get; private set; }
    public string FullName { get; private set; } = null!;
    public string Rfc { get; private set; } = null!;
    public decimal MonthlyIncome { get; private set; }
}

public class ApplicationDocument
{
    private ApplicationDocument()
    {
    }

    public ApplicationDocument(DocumentType type, string fileName, DateTime uploadedAt)
    {
        Type = type;
        FileName = fileName;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid LoanApplicationId { get; private set; }
    public DocumentType Type { get; private set; }
    public string FileName { get; private set; } = null!;
    public DateTime UploadedAt { get; private set; }
}

/// <summary>Bitácora inmutable de cambios de estado (quién, cuándo, de dónde a dónde).</summary>
public class StatusChange
{
    private StatusChange()
    {
    }

    public StatusChange(ApplicationStatus? from, ApplicationStatus to, Guid actorId, DateTime at, string? note)
    {
        From = from;
        To = to;
        ActorId = actorId;
        At = at;
        Note = note;
    }

    public long Id { get; private set; }
    public Guid LoanApplicationId { get; private set; }
    public ApplicationStatus? From { get; private set; }
    public ApplicationStatus To { get; private set; }
    public Guid ActorId { get; private set; }
    public DateTime At { get; private set; }
    public string? Note { get; private set; }
}
