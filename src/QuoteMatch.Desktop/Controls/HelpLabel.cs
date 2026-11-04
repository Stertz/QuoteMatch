using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace QuoteMatch.Desktop.Controls;

public sealed class HelpLabel : UserControl
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<HelpLabel, string>(nameof(Label), string.Empty);
    public static readonly StyledProperty<string> HelpProperty = AvaloniaProperty.Register<HelpLabel, string>(nameof(Help), string.Empty);
    public static readonly StyledProperty<string> SummaryProperty = AvaloniaProperty.Register<HelpLabel, string>(nameof(Summary), string.Empty);
    public static readonly DirectProperty<HelpLabel, string> TooltipTextProperty = AvaloniaProperty.RegisterDirect<HelpLabel, string>(nameof(TooltipText), label => label.TooltipText);
    private string _tooltipText = string.Empty;
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Help { get => GetValue(HelpProperty); set => SetValue(HelpProperty, value); }
    public string Summary { get => GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
    public string TooltipText => _tooltipText;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HelpProperty || change.Property == SummaryProperty)
            SetAndRaise(TooltipTextProperty, ref _tooltipText, string.IsNullOrWhiteSpace(Summary) ? Help : Summary);
    }
    public HelpLabel()
    {
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        TextBlock label = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(Label)) { Source = this });
        TextBlock explanation = new() { MaxWidth = 420, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        explanation.Bind(TextBlock.TextProperty, new Binding(nameof(Help)) { Source = this });
        Button button = new() { Content = "?", Classes = { "help" }, Flyout = new Flyout { Content = explanation } };
        button.Bind(ToolTip.TipProperty, new Binding(nameof(TooltipText)) { Source = this });
        button.Bind(AutomationProperties.NameProperty, new Binding(nameof(Help)) { Source = this });
        Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2, Children = { label, button } };
    }
}
