namespace JTS_App.Services;

public sealed record D365Options(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string EnvironmentUrl)
{
    public string NormalizedEnvironmentUrl => EnvironmentUrl.Trim().TrimEnd('/');

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(EnvironmentUrl);
}
