using System.Security.Cryptography;
using System.Text;
using Galbox.Core.Api;

// Throw-away diagnostic harness: reproduces exactly what A62 does, but without swallowing the
// exception that MoyuDpapiKeyStore.Get() catches internally. Evidence only; not part of the product.

var scratch = Path.Combine(Path.GetTempPath(), "GalboxMoyuProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var storePath = Path.Combine(scratch, "secrets", "moyu-api-key.bin");

const string key = "nmk_live_A62DPAPIprobe_DO_NOT_USE_0000000000";

var store = new MoyuDpapiKeyStore(storePath);
Console.WriteLine($"store path       : {storePath}");
Console.WriteLine($"Kind             : {store.Kind}");
Console.WriteLine($"IsConfigured(0)  : {store.IsConfigured}");

store.Set(key);
Console.WriteLine($"file exists      : {File.Exists(storePath)}");
var raw = File.ReadAllBytes(storePath);
Console.WriteLine($"raw length       : {raw.Length}");
Console.WriteLine($"IsConfigured(1)  : {store.IsConfigured}");
Console.WriteLine($"Get()            : {(store.Get() is null ? "(null)" : "non-null")}");

// Re-run Unprotect by hand against the same layout the store writes, so the real exception surfaces.
var offset = Encoding.ASCII.GetBytes("GALBOX-MOYU-KEY").Length + 1;
var ciphertext = raw[offset..];
var entropy = Encoding.UTF8.GetBytes("Galbox.Moyu.nmk.v1:developer.nextmoe.dev");

Console.WriteLine();
Console.WriteLine("--- raw Unprotect with the store's own entropy ---");
try
{
    var plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
    Console.WriteLine($"OK, {plaintext.Length} bytes, matches: {Encoding.UTF8.GetString(plaintext) == key}");
}
catch (Exception ex)
{
    Console.WriteLine($"THREW {ex.GetType().FullName}: {ex.Message}");
    Console.WriteLine($"HResult: 0x{ex.HResult:X8}");
}

Console.WriteLine();
Console.WriteLine("--- raw Unprotect with null entropy (what the A62 check does) ---");
try
{
    var plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
    Console.WriteLine($"OK, {plaintext.Length} bytes, matches: {Encoding.UTF8.GetString(plaintext) == key}");
}
catch (Exception ex)
{
    Console.WriteLine($"THREW {ex.GetType().FullName}: {ex.Message}");
    Console.WriteLine($"HResult: 0x{ex.HResult:X8}");
}

Console.WriteLine();
Console.WriteLine("--- one-shot Protect/Unprotect with the same entropy (baseline) ---");
try
{
    var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), entropy, DataProtectionScope.CurrentUser);
    Console.WriteLine($"protect OK, {blob.Length} bytes");
    var back = ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
    Console.WriteLine($"unprotect OK, matches: {Encoding.UTF8.GetString(back) == key}");
}
catch (Exception ex)
{
    Console.WriteLine($"THREW {ex.GetType().FullName}: {ex.Message}");
}

Directory.Delete(scratch, recursive: true);
Console.WriteLine();
Console.WriteLine("PROBE DONE");
