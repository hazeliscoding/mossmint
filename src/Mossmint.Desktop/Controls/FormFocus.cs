using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Mossmint.Desktop.Controls;

// Keeps keyboard focus somewhere useful when part of a page appears, disappears or is
// rebuilt, so a keyboard or Narrator user never lands on nothing (WCAG 2.4.3).
// IsForm: an inline form takes focus when it opens and hands it back when it closes.
// Target: a page names the control to focus next, by its accessible name.
public static class FormFocus
{
    public static readonly AttachedProperty<bool> IsFormProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsForm", typeof(FormFocus));

    public static readonly AttachedProperty<string?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Target", typeof(FormFocus));

    // The name is kept apart: a rebuilt opener's bindings are gone once it's detached.
    private static readonly ConditionalWeakTable<Control, Opener> Openers = [];

    // The button that opens a form is often hidden by it, which hands focus to a container
    // before the form appears. The last named control to have focus is the one that opened it.
    private static WeakReference<InputElement>? _lastNamed;

    static FormFocus()
    {
        // GotFocus bubbles; only the element that took focus counts, not its named ancestors.
        InputElement.GotFocusEvent.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.Source == element && Name(element) is not null)
            {
                _lastNamed = new(element);
            }
        });
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>(OnVisibleChanged);
        TargetProperty.Changed.AddClassHandler<Control>((page, e) =>
        {
            if (e.GetNewValue<string?>() is { } name && TopLevel.GetTopLevel(page) is { } top)
            {
                Dispatcher.UIThread.Post(() => FocusByName(top, name), DispatcherPriority.Loaded);
            }
        });
    }

    public static bool GetIsForm(Control control) => control.GetValue(IsFormProperty);

    public static void SetIsForm(Control control, bool value) => control.SetValue(IsFormProperty, value);

    public static string? GetTarget(Control control) => control.GetValue(TargetProperty);

    public static void SetTarget(Control control, string? value) => control.SetValue(TargetProperty, value);

    private static void OnVisibleChanged(Control form, AvaloniaPropertyChangedEventArgs e)
    {
        if (!GetIsForm(form) || TopLevel.GetTopLevel(form) is not { } top)
        {
            return;
        }

        if (e.GetNewValue<bool>())
        {
            if (_lastNamed?.TryGetTarget(out var opener) == true && !form.IsVisualAncestorOf(opener))
            {
                Openers.AddOrUpdate(form, new Opener(opener, Name(opener)));
            }

            Dispatcher.UIThread.Post(() => FirstField(form)?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }
        else if (Openers.TryGetValue(form, out var opener))
        {
            Openers.Remove(form);

            // Only when focus was in the form: a form closed by leaving the page mustn't
            // pull focus back from where the user went.
            if (top.FocusManager?.GetFocusedElement() is not Visual focused || form.IsVisualAncestorOf(focused))
            {
                Dispatcher.UIThread.Post(() => Return(top, opener), DispatcherPriority.Loaded);
            }
        }
    }

    // The opener may have been rebuilt meanwhile (a card or row redrawn after saving):
    // its replacement carries the same accessible name.
    private static void Return(TopLevel top, Opener opener)
    {
        if (opener.Element.IsAttachedToVisualTree() && opener.Element.IsEffectivelyVisible)
        {
            opener.Element.Focus(NavigationMethod.Tab);
        }
        else if (opener.Name is { } name)
        {
            FocusByName(top, name);
        }
    }

    private static void FocusByName(TopLevel top, string name) =>
        top.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && Name(c) == name)
            ?.Focus(NavigationMethod.Tab);

    private static string? Name(InputElement control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name : (control as ContentControl)?.Content as string;

    private sealed record Opener(InputElement Element, string? Name);

    private static InputElement? FirstField(Control form) =>
        form.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c is TextBox or ComboBox or CalendarDatePicker or ToggleSwitch);
}
