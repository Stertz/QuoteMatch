using Avalonia.Threading;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Domain.Integration;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ProductSearch_HidesInactiveByDefaultAndAutomaticallyRefreshesWhenToggled(int mode)
    {
        await session.Session.Dispatch(async () =>
        {
            SearchClock clock = new();
            MixedStateSearch lookup = new();
            using var model = new ProductSearchViewModel(new Localizer(), lookup, clock) { ModeIndex = mode, SearchText = "synthetic" };
            Assert.False(model.IncludeInactive);
            await model.SearchCommand.ExecuteAsync(null);
            Assert.Equal("000", Assert.Single(model.Results).Code);
            Assert.Equal(model.Strings.Format("ProductSearchLimited", 1), model.Status);
            model.IncludeInactive = true;
            Assert.Empty(model.Results);
            clock.Fire(); await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, model.Results.Count);
            model.SelectedProduct = model.Results[1]; Assert.False(model.CanSelect);
            model.IncludeInactive = false;
            Assert.Null(model.SelectedProduct); Assert.Empty(model.Results);
            clock.Fire(); await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("000", Assert.Single(model.Results).Code);
            model.SelectedProduct = model.Results[0]; Assert.True(model.CanSelect);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class MixedStateSearch : IErpProductSearch
    {
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ErpProductCandidate>>(Enumerable.Range(0, 100).Select(index => new MappingLookup().Product with
            { ProductCode = index.ToString("000", System.Globalization.CultureInfo.InvariantCulture), IsInactive = index > 0 }).ToArray());
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => FindByProductCodeAsync(name, token);
    }

    [Fact]
    public async Task ProductSearch_DebouncesTypingAndCancelsOnClose()
    {
        await session.Session.Dispatch(async () =>
        {
            SearchClock clock = new();
            MappingLookup lookup = new();
            using var model = new ProductSearchViewModel(new Localizer(), lookup, clock) { ModeIndex = 1 };
            model.SearchText = "ca";
            Assert.Equal(model.Strings["ProductSearchMinimum"], model.Status);
            model.SearchText = "cabo";
            model.SearchText = "cabo flex azul";
            Assert.Null(lookup.NameRequest);
            clock.Fire(); await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("cabo flex azul", lookup.NameRequest);
            Assert.Single(model.Results);
            model.SearchText = "another product";
            Assert.Empty(model.Results);
            model.Dispose(); clock.Fire(); await Task.Yield(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("cabo flex azul", lookup.NameRequest);
            Assert.Empty(model.Results);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProductSearch_LatestQueryWinsEvenWhenProviderIgnoresCancellation()
    {
        await session.Session.Dispatch(async () =>
        {
            DelayedSearch lookup = new();
            using var model = new ProductSearchViewModel(new Localizer(), lookup, new SearchClock());
            model.SearchText = "old";
            var old = model.SearchCommand.ExecuteAsync(null);
            Assert.True(model.IsBusy);
            model.SearchText = "new";
            Assert.True(lookup.FirstToken.IsCancellationRequested);
            await model.SearchCommand.ExecuteAsync(null);
            Assert.Equal("new", Assert.Single(model.Results).Code);
            string status = model.Status;
            lookup.First.SetResult([new MappingLookup().Product with { ProductCode = "old" }]);
            await old;
            Assert.Equal("new", Assert.Single(model.Results).Code);
            Assert.Equal(status, model.Status);
            Assert.False(model.IsBusy);
            model.SearchText = "canceled";
            model.CancelSearchCommand.Execute(null);
            Assert.Equal(model.Strings["ImportCanceled"], model.Status);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class DelayedSearch : IErpProductSearch
    {
        public TaskCompletionSource<IReadOnlyList<ErpProductCandidate>> First { get; } = new();
        public CancellationToken FirstToken { get; private set; }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token)
        {
            if (code == "old") { FirstToken = token; return First.Task; }
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>([new MappingLookup().Product with { ProductCode = code }]);
        }
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => FindByProductCodeAsync(name, token);
    }

    private sealed class SearchClock : TimeProvider
    {
        private readonly List<SearchTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(350), dueTime);
            SearchTimer timer = new(callback, state); _timers.Add(timer); return timer;
        }
        public void Fire() { foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class SearchTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) { _disposed = true; callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
