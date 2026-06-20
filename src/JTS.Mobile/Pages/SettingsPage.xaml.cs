using JTS.Mobile.Services;

namespace JTS.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly DataverseMobileService _dataverse;

    public SettingsPage(DataverseMobileService dataverse)
    {
        _dataverse = dataverse;
        InitializeComponent();
        foreach (var model in new[] { "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-reasoner", "deepseek-chat" })
            DeepSeekModelPicker.Items.Add(model);
        DeepSeekModelPicker.SelectedIndex = 0;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _dataverse.GetSettingsAsync();
        EnvironmentEntry.Text = settings.EnvironmentUrl;
        TenantEntry.Text = settings.TenantId;
        ClientEntry.Text = settings.ClientId;
        SecretEntry.Text = settings.ClientSecret;
        DeepSeekKeyEntry.Text = settings.DeepSeekApiKey;
        var modelIdx = DeepSeekModelPicker.Items.IndexOf(settings.DeepSeekModel);
        DeepSeekModelPicker.SelectedIndex = modelIdx >= 0 ? modelIdx : 0;
        DeepSeekThinkingCheckBox.IsChecked = settings.DeepSeekThinking;
    }

    private async void OnSaveSettings(object? sender, EventArgs e)
    {
        var secret = NormalizeSecret(SecretEntry.Text);
        if (Guid.TryParse(secret, out _))
        {
            await DisplayAlertAsync("Client secret", "That value looks like a Secret ID. I need the secret VALUE, the one Azure only shows when you create it.", "OK");
            return;
        }

        await _dataverse.SaveSettingsAsync(new MobileSettings(
            TenantEntry.Text ?? string.Empty,
            ClientEntry.Text ?? string.Empty,
            secret,
            EnvironmentEntry.Text ?? string.Empty,
            DeepSeekKeyEntry.Text ?? string.Empty,
            DeepSeekModelPicker.SelectedItem as string ?? "deepseek-v4-flash",
            DeepSeekThinkingCheckBox.IsChecked));

        SecretEntry.Text = secret;
        SecretHintLabel.Text = $"Secret saved ({secret.Length} characters). Testing connection...";
        StatusLabel.Text = "Saving...";

        try
        {
            await _dataverse.GetProjectsAsync();
            StatusLabel.Text = "Connection OK.";
            SecretHintLabel.Text = $"Secret saved ({secret.Length} characters).";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Connection error.";
            await DisplayAlertAsync("Dataverse", ex.Message, "OK");
        }
    }

    private async void OnPasteSecret(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        SecretEntry.Text = NormalizeSecret(text);
        SecretHintLabel.Text = string.IsNullOrEmpty(SecretEntry.Text)
            ? "No secret on the clipboard."
            : $"Secret pasted ({SecretEntry.Text.Length} characters).";
    }

    private static string NormalizeSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().Trim('"', '\'')
            .Where(c => !char.IsWhiteSpace(c) && c != '​' && c != '‌' && c != '‍' && c != '﻿');
        return new string(chars.ToArray());
    }
}
