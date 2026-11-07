// Firma y comprueba SHA256.json de una version (ECDSA P-256 + SHA-256; la app trae la clave publica).
//   dotnet run packaging/ReleaseSigning.cs sign   <SHA256.json> [update-signing.key]   -> escribe <SHA256.json>.sig
//   dotnet run packaging/ReleaseSigning.cs verify <SHA256.json> [update-signing.pub]   -> exit 0 si la firma es valida
// La clave privada vive en Documentos\SwitchMonitor-firma (la crea packaging\NewUpdateKey.cs); nunca va al repositorio.
using System.Security.Cryptography;

string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SwitchMonitor-firma");
if (args.Length < 2 || args[0] is not ("sign" or "verify")) { Console.Error.WriteLine("Uso: sign|verify <SHA256.json> [clave]"); return 2; }
byte[] manifest = File.ReadAllBytes(args[1]);
string sig = args[1] + ".sig";
if (args[0] == "sign")
{
    string keyFile = args.Length > 2 ? args[2] : Path.Combine(folder, "update-signing.key");
    using var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyFile).Trim()), out _);
    File.WriteAllText(sig, Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256)));
    Console.WriteLine($"Firmado: {sig}");
    return 0;
}
string pubFile = args.Length > 2 ? args[2] : Path.Combine(folder, "update-signing.pub");
using var pub = ECDsa.Create();
pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(File.ReadAllText(pubFile).Trim()), out _);
bool ok = pub.VerifyData(manifest, Convert.FromBase64String(File.ReadAllText(sig).Trim()), HashAlgorithmName.SHA256);
Console.WriteLine(ok ? "Firma valida." : "FIRMA NO VALIDA.");
return ok ? 0 : 1;
