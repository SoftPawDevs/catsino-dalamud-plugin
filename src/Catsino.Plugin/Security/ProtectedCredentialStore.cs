using System.Security.Cryptography;
using System.Text;

namespace Catsino.Plugin.Security;

public interface IProtectedCredentialStore
{
    Task StoreAsync(string credential, CancellationToken cancellationToken = default);

    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class DpapiCredentialStore(string filePath) : IProtectedCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Catsino.Plugin.RefreshCredential.v1");

    public async Task StoreAsync(string credential, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var directory = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("Credential path has no directory.");
        Directory.CreateDirectory(directory);

        var plaintext = Encoding.UTF8.GetBytes(credential);
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }
}
