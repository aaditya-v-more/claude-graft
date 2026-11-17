using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClaudeGraft;

/// An empty page a Claude's content is moved into, so switching Claudes plays a Frame's own transition.
public sealed partial class ClaudeHostPage : Page
{
    public ClaudeHostPage() => InitializeComponent();

    // Given here, before the page is shown, so the transition has the content to move;
    // set after navigating, the page entered empty and the content simply appeared.
    protected override void OnNavigatedTo(NavigationEventArgs e) => Content = e.Parameter as UIElement;
}
