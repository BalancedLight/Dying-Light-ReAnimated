using System.Windows;
using System.Windows.Controls;
namespace ReAnimated.App.Infrastructure;
public sealed partial class WindowsProjectFileDialogService
{
    public CustomModelSkeletonChoice SelectCustomModelSkeleton()
    {
        CustomModelSkeletonChoice result = CustomModelSkeletonChoice.Cancel;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Choose your model's skeleton", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Map to Dying Light to use its stock animations. Your model's body proportions and skinning are preserved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        var dialog = new Window { Title = "Import model", Width = 490, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner is not null) dialog.Owner = owner;
        foreach (var (choice, label) in new[] { (CustomModelSkeletonChoice.MapToDl1, "Map to Dying Light (recommended)"),
            (CustomModelSkeletonChoice.KeepOriginal, "Keep original skeleton"), (CustomModelSkeletonChoice.Cancel, "Cancel") })
        {
            var button = new Button { Content = label, MinHeight = 38, Margin = new Thickness(0, 0, 0, 8),
                IsDefault = choice == CustomModelSkeletonChoice.MapToDl1, IsCancel = choice == CustomModelSkeletonChoice.Cancel };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = choice != CustomModelSkeletonChoice.Cancel; };
            panel.Children.Add(button);
        }
        dialog.ShowDialog();
        return result;
    }
}
