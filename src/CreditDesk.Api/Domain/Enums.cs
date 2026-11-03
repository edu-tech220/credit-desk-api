namespace CreditDesk.Api.Domain;

public enum ApplicationStatus
{
    Draft = 0,
    Submitted = 1,
    InReview = 2,
    Approved = 3,
    Rejected = 4,
    Cancelled = 5,
    Disbursed = 6
}

public enum DocumentType
{
    OfficialId = 0,
    ProofOfAddress = 1,
    ProofOfIncome = 2,
    BankStatement = 3
}

public static class Roles
{
    public const string Applicant = "Applicant";
    public const string Analyst = "Analyst";
}
