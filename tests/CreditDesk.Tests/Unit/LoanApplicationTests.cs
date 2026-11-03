using CreditDesk.Api.Domain;

namespace CreditDesk.Tests.Unit;

public class LoanApplicationTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ApplicantId = Guid.NewGuid();
    private static readonly Guid AnalystId = Guid.NewGuid();

    private static LoanProduct Personal() => new()
    {
        Id = 1,
        Code = "PERSONAL",
        Name = "Crédito personal",
        MinAmount = 5_000m,
        MaxAmount = 300_000m,
        AnnualInterestRate = 0.32m,
        MaxPaymentToIncomeRatio = 0.35m,
        GuarantorThreshold = 100_000m,
        AllowedTerms = [6, 12, 18, 24, 36],
        RequiredDocuments = [DocumentType.OfficialId, DocumentType.ProofOfAddress, DocumentType.ProofOfIncome],
    };

    private static LoanApplication Draft(decimal amount = 50_000m, int term = 12, decimal income = 20_000m) =>
        LoanApplication.Create(ApplicantId, Personal(), amount, term, income, Now);

    private static void AttachAllDocuments(LoanApplication application)
    {
        foreach (var type in application.Product.RequiredDocuments)
            application.AttachDocument(ApplicantId, type, $"{type}.pdf", Now);
    }

    private static LoanApplication Submitted()
    {
        var application = Draft();
        AttachAllDocuments(application);
        application.Submit(ApplicantId, Now);
        return application;
    }

    private static LoanApplication InReview()
    {
        var application = Submitted();
        application.StartReview(AnalystId, Now);
        return application;
    }

    [Fact]
    public void Create_starts_in_draft_with_history_entry()
    {
        var application = Draft();

        Assert.Equal(ApplicationStatus.Draft, application.Status);
        Assert.True(application.IsOpen);
        var entry = Assert.Single(application.History);
        Assert.Null(entry.From);
        Assert.Equal(ApplicationStatus.Draft, entry.To);
    }

    [Theory]
    [InlineData(1_000, 12)]
    [InlineData(500_000, 12)]
    [InlineData(50_000, 7)]
    public void Create_rejects_amount_or_term_outside_product_limits(decimal amount, int term)
    {
        Assert.Throws<BusinessRuleException>(() => Draft(amount, term));
    }

    [Fact]
    public void Create_rejects_inactive_product()
    {
        var product = Personal();
        product.IsActive = false;

        var ex = Assert.Throws<ConflictException>(() =>
            LoanApplication.Create(ApplicantId, product, 50_000m, 12, 20_000m, Now));
        Assert.Equal("product_inactive", ex.Code);
    }

    [Fact]
    public void Submit_reports_all_missing_documents_at_once()
    {
        var application = Draft();
        application.AttachDocument(ApplicantId, DocumentType.OfficialId, "ine.pdf", Now);

        var ex = Assert.Throws<BusinessRuleException>(() => application.Submit(ApplicantId, Now));

        var message = Assert.Single(ex.Errors["documents"]);
        Assert.Contains(nameof(DocumentType.ProofOfAddress), message);
        Assert.Contains(nameof(DocumentType.ProofOfIncome), message);
        Assert.Equal(ApplicationStatus.Draft, application.Status);
    }

    [Fact]
    public void Submit_rejects_payment_above_income_ratio()
    {
        var application = Draft(income: 10_000m); // mensualidad ~4,900 > 35 % de 10,000
        AttachAllDocuments(application);

        var ex = Assert.Throws<BusinessRuleException>(() => application.Submit(ApplicantId, Now));

        Assert.True(ex.Errors.ContainsKey("monthlyIncome"));
    }

    [Fact]
    public void Submit_requires_guarantor_above_threshold()
    {
        var application = Draft(amount: 150_000m, term: 24, income: 40_000m);
        AttachAllDocuments(application);

        var ex = Assert.Throws<BusinessRuleException>(() => application.Submit(ApplicantId, Now));

        Assert.True(ex.Errors.ContainsKey("guarantors"));
    }

    [Fact]
    public void Submit_requires_guarantors_income_to_cover_one_payment()
    {
        var application = Draft(amount: 150_000m, term: 24, income: 40_000m);
        AttachAllDocuments(application);
        application.AddGuarantor(ApplicantId, "Aval Uno", "GODE561231GR8", 5_000m, Now);

        Assert.Throws<BusinessRuleException>(() => application.Submit(ApplicantId, Now));

        application.AddGuarantor(ApplicantId, "Aval Dos", "PEMA800101AB1", 5_000m, Now);
        application.Submit(ApplicantId, Now);
        Assert.Equal(ApplicationStatus.Submitted, application.Status);
    }

    [Fact]
    public void Submit_moves_to_submitted_and_changes_version()
    {
        var application = Draft();
        AttachAllDocuments(application);
        var before = application.Version;

        application.Submit(ApplicantId, Now.AddMinutes(5));

        Assert.Equal(ApplicationStatus.Submitted, application.Status);
        Assert.Equal(Now.AddMinutes(5), application.SubmittedAt);
        Assert.NotEqual(before, application.Version);
    }

    [Fact]
    public void Only_the_owner_can_modify_or_submit()
    {
        var application = Draft();
        var stranger = Guid.NewGuid();

        Assert.Throws<ForbiddenException>(() => application.UpdateTerms(stranger, 60_000m, 12, 20_000m, Now));
        Assert.Throws<ForbiddenException>(() => application.Submit(stranger, Now));
        Assert.Throws<ForbiddenException>(() => application.Cancel(stranger, Now));
    }

    [Fact]
    public void Submitted_application_is_no_longer_editable()
    {
        var application = Submitted();

        var ex = Assert.Throws<ConflictException>(() =>
            application.AttachDocument(ApplicantId, DocumentType.BankStatement, "estado.pdf", Now));
        Assert.Equal("not_editable", ex.Code);
    }

    [Fact]
    public void Attaching_same_document_type_replaces_previous_file()
    {
        var application = Draft();
        application.AttachDocument(ApplicantId, DocumentType.OfficialId, "ine-viejo.pdf", Now);
        application.AttachDocument(ApplicantId, DocumentType.OfficialId, "ine-nuevo.pdf", Now);

        var document = Assert.Single(application.Documents);
        Assert.Equal("ine-nuevo.pdf", document.FileName);
    }

    [Theory]
    [InlineData("XXX")]
    [InlineData("GODE561231")]
    [InlineData("1234567890123")]
    public void AddGuarantor_validates_rfc_format(string rfc)
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            Draft().AddGuarantor(ApplicantId, "Aval", rfc, 10_000m, Now));
        Assert.True(ex.Errors.ContainsKey("rfc"));
    }

    [Fact]
    public void AddGuarantor_rejects_duplicates_and_enforces_limit()
    {
        var application = Draft();
        application.AddGuarantor(ApplicantId, "Aval Uno", "gode561231gr8", 10_000m, Now);

        var duplicated = Assert.Throws<ConflictException>(() =>
            application.AddGuarantor(ApplicantId, "Otro", "GODE561231GR8", 10_000m, Now));
        Assert.Equal("guarantor_duplicated", duplicated.Code);

        application.AddGuarantor(ApplicantId, "Aval Dos", "PEMA800101AB1", 10_000m, Now);
        var limit = Assert.Throws<ConflictException>(() =>
            application.AddGuarantor(ApplicantId, "Aval Tres", "LOPE900101XY2", 10_000m, Now));
        Assert.Equal("guarantor_limit", limit.Code);
    }

    [Fact]
    public void Review_assigns_analyst_and_only_that_analyst_can_decide()
    {
        var application = InReview();

        Assert.Equal(AnalystId, application.AssignedAnalystId);
        Assert.Throws<ForbiddenException>(() => application.Approve(Guid.NewGuid(), null, Now));

        application.Approve(AnalystId, "Buen historial", Now);
        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.Equal("Buen historial", application.DecisionNote);
    }

    [Fact]
    public void Cannot_approve_without_review()
    {
        var ex = Assert.Throws<ConflictException>(() => Submitted().Approve(AnalystId, null, Now));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("corto")]
    public void Reject_requires_meaningful_reason(string reason)
    {
        var application = InReview();

        Assert.Throws<BusinessRuleException>(() => application.Reject(AnalystId, reason, Now));
        Assert.Equal(ApplicationStatus.InReview, application.Status);
    }

    [Fact]
    public void Rejected_application_is_closed_and_terminal()
    {
        var application = InReview();
        application.Reject(AnalystId, "Capacidad de pago insuficiente", Now);

        Assert.False(application.IsOpen);
        Assert.True(LoanApplication.IsTerminal(application.Status));
        Assert.Throws<ConflictException>(() => application.Disburse(AnalystId, Now));
    }

    [Fact]
    public void Applicant_can_cancel_while_submitted_but_not_during_review()
    {
        var submitted = Submitted();
        submitted.Cancel(ApplicantId, Now);
        Assert.Equal(ApplicationStatus.Cancelled, submitted.Status);
        Assert.False(submitted.IsOpen);

        Assert.Throws<ConflictException>(() => InReview().Cancel(ApplicantId, Now));
    }

    [Fact]
    public void Full_lifecycle_is_recorded_in_history()
    {
        var application = InReview();
        application.Approve(AnalystId, null, Now);
        application.Disburse(AnalystId, Now);

        Assert.Equal(
            [ApplicationStatus.Draft, ApplicationStatus.Submitted, ApplicationStatus.InReview,
             ApplicationStatus.Approved, ApplicationStatus.Disbursed],
            application.History.Select(h => h.To));
        Assert.False(application.IsOpen);
    }
}
