using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace SCSKiller.App.Controls;

/// <summary>The count is already in its owner's automation name: its own text stays out of the UI Automation tree.</summary>
public sealed class QuietInfoBadge : InfoBadge
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("ValueTextBlock") is TextBlock value)
            AutomationProperties.SetAccessibilityView(value, AccessibilityView.Raw);
    }
}
