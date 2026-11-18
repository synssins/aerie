using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Kb.Data;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// The keys that protect the session cookie and every stored secret are kept in the database. With the secret
/// <see cref="SecretName"/> set in configuration (an environment variable, a host secret; never the database) they are stored
/// encrypted with it, so a copy of the database alone opens nothing. Without the secret they are stored as before.
/// Whoever loses the secret loses the stored secrets and every session: the keys cannot be read without it.
/// </summary>
public static class KeyProtection
{
    public const string SecretName = "Kb--KeyProtectionSecret";
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("kb.keyring.v1");

    private static byte[] KeyOf(string secret) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), Salt, 200_000, HashAlgorithmName.SHA256, 32);

    /// <summary>Wraps with AES-256-GCM under a key made from the secret.</summary>
    public sealed class Encryptor(string secret) : IXmlEncryptor
    {
        private readonly byte[] _key = KeyOf(secret);

        public EncryptedXmlInfo Encrypt(XElement plaintextElement)
        {
            ArgumentNullException.ThrowIfNull(plaintextElement);
            var plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var (cipher, tag) = (new byte[plain.Length], new byte[16]);
            using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            return new EncryptedXmlInfo(new XElement("wrapped", Convert.ToBase64String([.. nonce, .. tag, .. cipher])), typeof(Decryptor));
        }
    }

    /// <summary>Made by the framework, by the type name written next to each wrapped key.</summary>
    public sealed class Decryptor(IServiceProvider services) : IXmlDecryptor
    {
        public XElement Decrypt(XElement encryptedElement)
        {
            ArgumentNullException.ThrowIfNull(encryptedElement);
            var secret = services.GetRequiredService<IConfiguration>()[SecretName];
            if (string.IsNullOrEmpty(secret)) throw new CryptographicException($"The keys are protected with {SecretName}, which is not set.");
            var all = Convert.FromBase64String(encryptedElement.Value);
            var plain = new byte[all.Length - 28];
            using (var aes = new AesGcm(KeyOf(secret), 16)) aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return XElement.Parse(Encoding.UTF8.GetString(plain));
        }
    }

    /// <summary>
    /// At start, before any key is used. With the secret: every key still stored in clear is wrapped, in place, the way the framework
    /// writes a new one. Without it: a database whose keys are wrapped stops the start with the reason, instead of running on a new key
    /// that reads none of the stored secrets.
    /// </summary>
    public static async Task OnStartupAsync(KbDbContext db, string? secret, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(log);
        var rows = await db.DataProtectionKeys.ToListAsync();
        var marker = typeof(Decryptor).FullName!;
        if (string.IsNullOrEmpty(secret))
        {
            if (rows.Any(r => r.Xml?.Contains(marker, StringComparison.Ordinal) == true))
                throw new InvalidOperationException($"The keys in the database are protected with {SecretName}, and it is not set. Set it to the value it had; without it no stored secret can be read.");
            log.LogWarning("The keys that protect sessions and stored secrets are in the database unencrypted. Set {Secret} to store them encrypted.", SecretName);
            return;
        }
        var encryptor = new Encryptor(secret);
        var wrapped = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Xml)) continue;
            var xml = XElement.Parse(row.Xml);
            var open = xml.Descendants().Where(e => (bool?)e.Attribute(Dp + "requiresEncryption") == true).ToList();
            if (open.Count == 0) continue;
            foreach (var e in open)
            {
                var info = encryptor.Encrypt(e);
                e.ReplaceWith(new XElement(Dp + "encryptedSecret", new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement));
            }
            row.Xml = xml.ToString(SaveOptions.DisableFormatting);
            wrapped++;
        }
        if (wrapped == 0) return;
        await db.SaveChangesAsync();
        log.LogInformation("{Count} keys that were stored unencrypted are now protected with {Secret}.", wrapped, SecretName);
    }
}
