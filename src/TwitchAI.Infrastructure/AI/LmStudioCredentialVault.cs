using Windows.Security.Credentials;

namespace TwitchAI.Infrastructure.AI;

public interface ILmStudioCredentialVault
{
    Task<string?> LoadTokenAsync(CancellationToken cancellationToken = default);

    Task SaveTokenAsync(string token, CancellationToken cancellationToken = default);

    Task DeleteTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class WindowsLmStudioCredentialVault : ILmStudioCredentialVault
{
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private readonly PasswordVault _vault = new();
    private readonly LmStudioOptions _options;

    public WindowsLmStudioCredentialVault(InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.LmStudio;
    }

    public Task<string?> LoadTokenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = _vault.Retrieve(
                _options.VaultResource,
                _options.VaultAccount);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (Exception exception) when (
            exception.HResult == ElementNotFoundHResult)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExisting();
        _vault.Add(
            new PasswordCredential(
                _options.VaultResource,
                _options.VaultAccount,
                token));
        return Task.CompletedTask;
    }

    public Task DeleteTokenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExisting();
        return Task.CompletedTask;
    }

    private void RemoveExisting()
    {
        try
        {
            var existing = _vault.Retrieve(
                _options.VaultResource,
                _options.VaultAccount);
            _vault.Remove(existing);
        }
        catch (Exception exception) when (
            exception.HResult == ElementNotFoundHResult)
        {
            // The optional local token has not been configured.
        }
    }
}
