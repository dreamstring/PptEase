using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
var assembly = Assembly.GetExecutingAssembly();
using var payload = assembly.GetManifestResourceStream("PptEase.Payload.zip")!;
var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
payload.Position = 0;
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PptEase", "Packages", hash);
using (var mutex = new Mutex(false, "Local\\PptEasePackage" + hash)) {
 mutex.WaitOne();
 try {
  if (!File.Exists(Path.Combine(root, ".complete"))) {
   Directory.CreateDirectory(root);
   using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
   archive.ExtractToDirectory(root, true);
   File.WriteAllText(Path.Combine(root, ".complete"), hash);
  }
 } finally { mutex.ReleaseMutex(); }
}
var start = new ProcessStartInfo(Path.Combine(root, "PptEase.exe")) { UseShellExecute = false, WorkingDirectory = root };
foreach (var arg in args) start.ArgumentList.Add(arg);
using var process = Process.Start(start)!;
process.WaitForExit();
return process.ExitCode;
