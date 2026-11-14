using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using ZenFiles.Models;
using ZenFiles.Services;

namespace ZenFiles.Views;

public static class Dialogs
{
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string text = "", string primary = "OK")
    {
        var box = new TextBox { Text = text, MinWidth = 340 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box,
            PrimaryButtonText = primary, CloseButtonText = "Avbryt", DefaultButton = ContentDialogButton.Primary
        };
        return await d.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim() : null;
    }

    public static async Task<string?> PasswordAsync(XamlRoot root, string title)
    {
        var box = new PasswordBox { MinWidth = 340 };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box,
            PrimaryButtonText = "Koble til", CloseButtonText = "Avbryt", DefaultButton = ContentDialogButton.Primary
        };
        return await d.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = "Avbryt", DefaultButton = ContentDialogButton.Close
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ErrorAsync(XamlRoot root, string title, string message)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK"
        };
        await d.ShowAsync();
    }
}

public static class ConnectionDialog
{
    public static async Task<ConnectionProfile?> ShowAsync(XamlRoot root, ConnectionProfile? existing = null)
    {
        var p = existing ?? new ConnectionProfile();

        var name = new TextBox { Header = "Navn", Text = p.Name, PlaceholderText = "Hjemmeserver" };
        var host = new TextBox { Header = "Vert (host eller IP)", Text = p.Host };
        var port = new NumberBox { Header = "Port", Value = p.Port, Minimum = 1, Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        var user = new TextBox { Header = "Brukernavn", Text = p.User };
        var useKey = new ToggleSwitch { Header = "Innlogging", IsOn = p.UseKey, OffContent = "Passord", OnContent = "Nøkkelfil" };
        var key = new TextBox { Header = "Sti til privatnøkkel", Text = p.KeyPath, PlaceholderText = @"C:\Users\deg\.ssh\id_ed25519" };
        var secret = new PasswordBox
        {
            Header = "Passord / nøkkel-passfrase (valgfritt, lagres kryptert med Windows)",
            PlaceholderText = string.IsNullOrEmpty(existing?.EncryptedSecret) ? "" : "(lagret – la stå tomt for å beholde)"
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.Children.Add(host);
        Grid.SetColumn(port, 1);
        row.Children.Add(port);
        panel.Children.Add(name);
        panel.Children.Add(row);
        panel.Children.Add(user);
        panel.Children.Add(useKey);
        panel.Children.Add(key);
        panel.Children.Add(secret);

        void Sync() => key.Visibility = useKey.IsOn ? Visibility.Visible : Visibility.Collapsed;
        useKey.Toggled += (_, _) => Sync();
        Sync();

        var d = new ContentDialog
        {
            XamlRoot = root,
            Title = existing == null ? "Ny SSH/SFTP-tilkobling" : "Rediger tilkobling",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Lagre", CloseButtonText = "Avbryt", DefaultButton = ContentDialogButton.Primary
        };

        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text)) return null;

        // Lagring av en tilkobling nullstiller lagret vertsnøkkel (første neste tilkobling stoles på på nytt).
        p.HostKeySha256 = null;

        p.Host = host.Text.Trim();
        p.Port = (int)port.Value;
        p.User = user.Text.Trim();
        p.Name = string.IsNullOrWhiteSpace(name.Text) ? $"{p.User}@{p.Host}" : name.Text.Trim();
        p.UseKey = useKey.IsOn;
        p.KeyPath = key.Text.Trim();
        if (!string.IsNullOrEmpty(secret.Password)) p.EncryptedSecret = ConnectionStore.Protect(secret.Password);
        return p;
    }
}
