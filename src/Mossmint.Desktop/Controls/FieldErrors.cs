using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Mossmint.Desktop.Controls;

// An error names its field by the field's label ("Amount: \"12,3\" isn't an amount."). That
// field gets the critical border, carries the message for Narrator, and takes focus, so the
// error is tied to what needs fixing (WCAG 3.3.1). Errors without a field prefix stay where
// they are, under the form.
public static class FieldErrors
{
    private static readonly ConditionalWeakTable<TextBlock, TextBox> Marked = [];

    public static void Install() =>
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((error, _) =>
        {
            if (error.Classes.Contains("error"))
            {
                // After layout, once the error is visible and its form is on screen.
                Dispatcher.UIThread.Post(() => Update(error), DispatcherPriority.Loaded);
            }
        });

    private static void Update(TextBlock error)
    {
        if (Marked.TryGetValue(error, out var previous))
        {
            previous.Classes.Remove("invalid");
            AutomationProperties.SetHelpText(previous, null);
            Marked.Remove(error);
        }

        if (error.Text is not { } text || text.IndexOf(':', StringComparison.Ordinal) is var colon && colon <= 0 || !error.IsEffectivelyVisible)
        {
            return;
        }

        // The exact label first; "APR" also finds "APR percent".
        var label = text[..colon];
        var scope = (Visual?)error.FindAncestorOfType<UserControl>() ?? TopLevel.GetTopLevel(error);
        var field = scope?.GetVisualDescendants().OfType<TextBox>()
            .Where(t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) is { } name && name.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => AutomationProperties.GetName(t)!.Length)
            .FirstOrDefault();
        if (field is null)
        {
            return;
        }

        field.Classes.Add("invalid");
        AutomationProperties.SetHelpText(field, text);
        Marked.Add(error, field);
        field.Focus(NavigationMethod.Tab);
    }
}
