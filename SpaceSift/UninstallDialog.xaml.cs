using System.ComponentModel;
using System.Windows;
using SpaceSift.Core;

namespace SpaceSift;

/// <summary>Asks: uninstall only, or uninstall and remove every connected folder.</summary>
public partial class UninstallDialog : Window
{
    public sealed class Choice : INotifyPropertyChanged
    {
        private bool _checked = true;
        public required RelatedItem Item { get; init; }
        public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<Choice> _choices;

    public UninstallDialog(AppInfo app, List<RelatedItem> related)
    {
        InitializeComponent();
        AppIcon.Source = app.Icon;
        TitleText.Text = $"Uninstall {app.Name}?";
        SubText.Text = string.Join(" · ", new[] { app.Publisher, app.Version, app.Size > 0 ? app.SizeText : "" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // registry keys are cleaned by the uninstaller itself; only offer things on disk
        _choices = related.Where(r => r.CanDelete).Select(r => new Choice { Item = r }).ToList();
        foreach (var c in _choices) c.PropertyChanged += (_, _) => UpdateTexts();
        ItemsList.ItemsSource = _choices;
        if (_choices.Count == 0)
        {
            OptAll.IsEnabled = false;
            ListBorder.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }
        UpdateTexts();
        // dialog content fades and glides in
        Loaded += (_, _) => { if (Content is UIElement c) SpaceSift.UI.Motion.FadeIn(c, 320, 12); };
    }

    /// <summary>True when the user picked "uninstall and remove all connected folders".</summary>
    public bool RemoveConnected => OptAll.IsChecked == true;

    public List<RelatedItem> SelectedItems => _choices.Where(c => c.IsChecked).Select(c => c.Item).ToList();

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (ListBorder == null) return;
        ListBorder.IsEnabled = OptAll.IsChecked == true;
        SpaceSift.UI.Motion.FadeTo(ListBorder, OptAll.IsChecked == true ? 1 : 0.55, 260);
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        if (OkText == null || _choices == null) return;
        long total = _choices.Where(c => c.IsChecked).Sum(c => c.Item.Size);
        OptAllTitle.Text = _choices.Count > 0
            ? $"Uninstall and remove all connected folders ({Fmt.Size(_choices.Sum(c => c.Item.Size))})"
            : "Uninstall and remove all connected folders";
        OkText.Text = RemoveConnected ? $"Uninstall + remove {Fmt.Size(total)}" : "Uninstall";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
