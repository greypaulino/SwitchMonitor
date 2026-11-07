// Crea (una sola vez) la clave que firma SHA256.json de cada version.  Uso:  dotnet run packaging/NewUpdateKey.cs
// La parte PRIVADA queda fuera del proyecto, junto a la clave del APK (Documentos\SwitchMonitor-firma\update-signing.key):
// quien la tenga puede firmar actualizaciones; no la subas a ningun sitio y guarda una copia.
// La parte PUBLICA se imprime: va incrustada en la app (src\SwitchMonitor.Core\Updates\UpdateKeys.cs).
using System.Security.Cryptography;

string folder = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SwitchMonitor-firma");
Directory.CreateDirectory(folder);
string priv = Path.Combine(folder, "update-signing.key"), pub = Path.Combine(folder, "update-signing.pub");
if (File.Exists(priv)) { Console.WriteLine($"Ya existe {priv}; no se cambia."); Console.WriteLine(File.ReadAllText(pub)); return; }
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
File.WriteAllText(priv, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
File.WriteAllText(pub, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
Console.WriteLine($"Clave privada: {priv} (copiala a un lugar seguro)");
Console.WriteLine("Clave publica (para UpdateKeys.cs):");
Console.WriteLine(File.ReadAllText(pub));
