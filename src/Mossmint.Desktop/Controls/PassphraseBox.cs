using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Mossmint.Desktop.Controls;

// A text box for passphrases and recovery keys. Avalonia's text box hands its text to UI
// Automation even with a PasswordChar, so any screen reader or other UIA client could read
// the passphrase as it's typed. This one is an edit field with no value to read.
public sealed class PassphraseBox : TextBox
{
    public PassphraseBox() => PasswordChar = '•';

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(PassphraseBox owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
    }
}
