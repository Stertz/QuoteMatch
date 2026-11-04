using QuoteMatch.Desktop.Localization;

namespace QuoteMatch.Desktop.ViewModels;

public enum DemandReviewAction { Cancel, Continue, Quantities, Reference }
public sealed record DemandWarningViewModel(Localizer Strings, int MissingCount, bool CanReview)
{
    public string Message => MissingCount == 1 ? Strings["MissingDemandWarningSingle"] : Strings.Format("MissingDemandWarning", MissingCount);
}
