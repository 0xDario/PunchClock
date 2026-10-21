using System.Globalization;
using System.Security.Cryptography;
using PunchClock.Core.Accounts;

namespace PunchClock.OwnerKey;

/// <summary>
/// The site owner's recovery key, kept on the owner's own computer and never on the kiosk:
/// <code>
/// PunchClock.OwnerKey new-key    &lt;key file&gt;               make a key; prints the public key for the app
/// PunchClock.OwnerKey sign       &lt;key file&gt; [challenge]   sign a kiosk's recovery challenge
/// PunchClock.OwnerKey public-key &lt;key file&gt;               print the public key again
/// </code>
/// The key file is an encrypted PKCS#8 ECDSA P-256 key; its passphrase is asked for each time.
/// Exit codes: 0 ok, 1 error, 2 usage, 4 stopped by the user.
/// </summary>
internal static class Program
{
    private const int MinPassphraseLength = 12;

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["new-key", var file] => NewKey(file),
                ["sign", var file] => Sign(file, Ask("Challenge code from the kiosk: ")),
                ["sign", var file, .. var code] => Sign(file, string.Join("", code)),
                ["public-key", var file] => PrintPublicKey(file),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int NewKey(string file)
    {
        var passphrase = AskSecret($"Passphrase for the new key ({MinPassphraseLength}+ characters): ");
        if (passphrase.Length < MinPassphraseLength)
        {
            Console.Error.WriteLine($"The passphrase must be at least {MinPassphraseLength} characters.");
            return 2;
        }

        if (AskSecret("Repeat the passphrase: ") != passphrase)
        {
            Console.Error.WriteLine("The passphrases do not match.");
            return 2;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        // CreateNew: never overwrite a key, which would lock out every kiosk that trusts it.
        using (var writer = new StreamWriter(file, options))
        {
            writer.Write(pem);
        }

        var publicKey = OwnerKeySignature.ExportPublicKey(key);
        Console.WriteLine($"""

            Saved the private key to {Path.GetFullPath(file)}.
            Keep it and its passphrase off the kiosk PC, and keep a backup copy offline (for example on a USB stick in a safe place).
            Without it, no admin account can be recovered.

            Public key (safe to share; it goes into PunchClock's OwnerKeys.Trusted):
            {publicKey}

            Fingerprint (shown in the audit log after a recovery): {OwnerKeySignature.Fingerprint(publicKey)}
            """);
        return 0;
    }

    private static int Sign(string file, string code)
    {
        var challenge = RecoveryChallenge.Parse(code);
        var age = DateTimeOffset.UtcNow - challenge.IssuedUtc;
        Console.WriteLine($"""

            Recovery challenge {challenge.Code}
              Admin account id: {challenge.UserId}
              Issued:           {challenge.IssuedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)} ({FormatAge(age)})
            """);
        if (age > AdminRecoveryService.ChallengeLifetime)
        {
            Console.WriteLine($"This challenge is more than {AdminRecoveryService.ChallengeLifetime.TotalMinutes:0} minutes old; the kiosk will refuse it. Ask for a new one.");
            return 4;
        }

        Console.WriteLine("Signing lets whoever is at that kiosk set a temporary password on this admin account.");
        Console.WriteLine("Only sign a challenge you asked for or that the account's owner gave you.");
        if (!string.Equals(Ask("Sign it? (y/N): ").Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        using var key = LoadKey(file);
        Console.WriteLine($"""

            Response code (type or paste it into the kiosk; it works only for this challenge):
            {OwnerKeySignature.Sign(key, challenge)}
            """);
        return 0;
    }

    private static int PrintPublicKey(string file)
    {
        using var key = LoadKey(file);
        var publicKey = OwnerKeySignature.ExportPublicKey(key);
        Console.WriteLine(publicKey);
        Console.WriteLine($"Fingerprint: {OwnerKeySignature.Fingerprint(publicKey)}");
        return 0;
    }

    private static ECDsa LoadKey(string file)
    {
        var pem = File.ReadAllText(file);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, AskSecret("Key passphrase: "));
            return key;
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new CryptographicException("Wrong passphrase, or not a PunchClock owner key file.", ex);
        }
    }

    private static string FormatAge(TimeSpan age) => age < TimeSpan.Zero
        ? "in the future by this computer's clock"
        : $"{(int)age.TotalMinutes} minutes ago";

    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    private static string AskSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            PunchClock owner key: recovers an admin account when no admin can sign in.

              PunchClock.OwnerKey new-key    <key file>               make a key; prints the public key for the app
              PunchClock.OwnerKey sign       <key file> [challenge]   sign a kiosk's recovery challenge
              PunchClock.OwnerKey public-key <key file>               print the public key again
            """);
        return 2;
    }
}
