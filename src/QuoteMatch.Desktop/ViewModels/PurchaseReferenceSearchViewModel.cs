using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Domain.Purchasing;

namespace QuoteMatch.Desktop.ViewModels;

public sealed record PurchaseReferenceCandidate(Guid QuotationId, string Label, string Detail, PurchaseOrderReference? Reference)
{
    public bool CanCompare => Reference is not null;
}

public partial class PurchaseReferenceSearchViewModel(Localizer strings, IReadOnlyList<PurchaseReferenceCandidate> candidates) : ObservableObject
{
    public Localizer Strings => strings;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelect)), NotifyPropertyChangedFor(nameof(CanPrepare))]
    private PurchaseReferenceCandidate? _selected;
    public ObservableCollection<PurchaseReferenceCandidate> Results { get; } = new(candidates);
    public bool CanSelect => Selected?.CanCompare == true;
    public bool CanPrepare => Selected is not null;
    public string Status => strings.Format("OrdersReferenceSearchCount", Results.Count);
    partial void OnSearchTextChanged(string value)
    {
        var old = Selected; Results.Clear();
        foreach (var row in candidates.Where(row => strings.Culture.CompareInfo.IndexOf(row.Label, value,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)) Results.Add(row);
        Selected = Results.Contains(old!) ? old : Results.FirstOrDefault(); OnPropertyChanged(nameof(Status));
    }
}

public sealed record PurchaseReferenceSearchResult(PurchaseReferenceCandidate Candidate, bool PreparePurchase);
