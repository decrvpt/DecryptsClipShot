using System.Windows;
using System.Windows.Input;

namespace DecryptsClipShot;

/// <summary>Asks the player for the real name of a weapon ClipShot doesn't know yet.</summary>
public partial class NameWeaponWindow : Window
{
    public string WeaponName => NameBox.Text.Trim();
    public bool IsSniper => SniperCheck.IsChecked == true;
    public bool HasScope => !IsSniper || ScopeCheck.IsChecked == true;
    public bool UsesHeavyAmmo => HeavyAmmoCheck.IsChecked == true;

    public NameWeaponWindow(string codename, string shownAs)
    {
        InitializeComponent();
        CodenameText.Text = $"Internal name: {codename}   (shown now as \"{shownAs}\")";
        bool sniper = shownAs.Contains("Sniper", System.StringComparison.OrdinalIgnoreCase);
        SniperCheck.IsChecked = sniper;
        // Every sniper in Fortnite's current pool uses heavy ammo except the Automatic Sniper.
        HeavyAmmoCheck.IsChecked = sniper;
        UpdateScopeOption();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => NameBox.Focus();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SaveButton.IsEnabled = WeaponName.Length > 0;

    private void SniperCheck_Changed(object sender, RoutedEventArgs e) => UpdateScopeOption();

    private void UpdateScopeOption()
    {
        if (ScopeCheck is null) return;
        ScopeCheck.IsEnabled = SniperCheck.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
