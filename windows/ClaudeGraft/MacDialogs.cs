using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClaudeGraft;

internal static class MacDialogs
{
    public static void Prepare(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["MacPrimaryButton"];
        dialog.SecondaryButtonStyle = dialog.CloseButtonStyle = (Style)Application.Current.Resources["MacButton"];
        dialog.Opened += (_, _) =>
        {
            var buttons = Descendants(dialog).OfType<Button>().Where(b => b.Name is "CloseButton" or "SecondaryButton" or "PrimaryButton").ToList();
            var command = Descendants(dialog).OfType<Grid>().FirstOrDefault(g => g.Name == "CommandSpace");
            if (command is null || buttons.Count == 0) return;
            // Preserve the dialog's own buttons and event handlers, while putting
            // Cancel before the affirmative action as in the macOS sheets.
            foreach (var button in buttons)
                if (VisualTreeHelper.GetParent(button) is Panel parent) parent.Children.Remove(button);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var name in new[] { "CloseButton", "SecondaryButton", "PrimaryButton" })
                if (buttons.FirstOrDefault(b => b.Name == name) is Button button)
                {
                    button.MinWidth = 72;
                    button.Height = 26;
                    button.IsTabStop = true;
                    row.Children.Add(button);
                }
            Grid.SetColumnSpan(row, Math.Max(1, command.ColumnDefinitions.Count));
            command.Children.Clear();
            command.Children.Add(row);
        };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
