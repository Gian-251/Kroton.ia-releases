using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Kroton
{
    internal static class GitHubUpdater
    {
        private const string ReleaseApi =
            "https://api.github.com/repos/Gian-251/Kroton.ia-releases/releases/latest";
        private const string PackageAssetName = "Install-Kroton.zip";
        private const int CheckIntervalHours = 6;
        private static int started;

        public static void StartIfEligible(string baseDir, Action onUpdaterStarted)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            if (IsDeveloperBuild(baseDir))
            {
                Log(baseDir, "Atualizacao automatica desativada para a copia de desenvolvimento.");
                return;
            }

            Thread worker = new Thread(delegate()
            {
                CheckAndUpdate(baseDir, onUpdaterStarted);
            });
            worker.IsBackground = true;
            worker.Name = "Kroton-GitHub-Update";
            worker.Start();
        }

        private static bool IsDeveloperBuild(string baseDir)
        {
            string marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"KrotonIA\developer-mode.txt");
            if (!File.Exists(marker)) return false;

            try
            {
                string markedPath = Path.GetFullPath(File.ReadAllText(marker).Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string currentPath = Path.GetFullPath(baseDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(markedPath, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log(baseDir, "Nao foi possivel validar o marcador de desenvolvedor: " + ex.Message);
                return false;
            }
        }

        private static void CheckAndUpdate(string baseDir, Action onUpdaterStarted)
        {
            string tempDir = null;
            try
            {
                if (!ShouldCheckNow(baseDir)) return;
                RecordCheckTime(baseDir);

                string json;
                using (WebClient client = CreateClient())
                {
                    json = client.DownloadString(ReleaseApi);
                }

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> release =
                    serializer.Deserialize<Dictionary<string, object>>(json);
                if (release == null) throw new InvalidDataException("Resposta de release invalida.");

                string tag = GetString(release, "tag_name");
                Version latestVersion = ParseVersion(tag);
                Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (latestVersion <= currentVersion)
                {
                    Log(baseDir, "Sem atualizacao: instalado " + currentVersion + ", release " + tag + ".");
                    return;
                }

                string assetUrl = FindPackageAsset(release);
                if (string.IsNullOrEmpty(assetUrl))
                    throw new InvalidDataException("Release " + tag + " nao contem " + PackageAssetName + ".");

                string localUpdates = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"KrotonIA\updates");
                tempDir = Path.Combine(localUpdates, SafeName(tag) + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string zipPath = Path.Combine(tempDir, PackageAssetName);
                using (WebClient client = CreateClient())
                {
                    client.DownloadFile(assetUrl, zipPath);
                }

                string stagePath = Path.Combine(tempDir, "package");
                ExtractPackageSafely(zipPath, stagePath);
                string updateScript = Path.Combine(stagePath, @"install\Apply-KrotonUpdate.ps1");
                string stagedExe = Path.Combine(stagePath, "KrotonIA.exe");
                if (!File.Exists(updateScript) || !File.Exists(stagedExe))
                    throw new InvalidDataException("Pacote incompleto: faltam KrotonIA.exe ou o script atualizador.");
                Version packageVersion = AssemblyName.GetAssemblyName(stagedExe).Version;
                if (packageVersion != latestVersion)
                    throw new InvalidDataException(
                        "Versao do pacote (" + packageVersion + ") nao corresponde a tag " + tag + ".");

                Log(baseDir, "Release " + tag + " baixada; aguardando modo normal e ausencia de jogos.");
                if (!WaitForSafeWindow(baseDir, TimeSpan.FromHours(24)))
                {
                    Log(baseDir, "Atualizacao adiada: nao foi possivel confirmar um periodo seguro em 24 horas.");
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    Quote(updateScript) + " -InstallPath " + Quote(baseDir) +
                    " -StagePath " + Quote(stagePath) +
                    " -ReleaseVersion " + Quote(tag) +
                    " -ParentPid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = stagePath;

                Process updater = Process.Start(psi);
                if (updater == null) throw new InvalidOperationException("Nao foi possivel iniciar o atualizador.");
                Log(baseDir, "Aplicacao da release " + tag + " iniciada; dados do usuario permanecem locais.");
                if (onUpdaterStarted != null) onUpdaterStarted();
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                    Log(baseDir, "Nenhuma release publicada ainda; atualizacao ignorada.");
                else
                    Log(baseDir, "Erro de rede ao verificar/baixar atualizacao: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log(baseDir, "Erro no auto-update: " + ex);
            }
        }

        private static WebClient CreateClient()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "KrotonIA-Updater";
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static bool ShouldCheckNow(string baseDir)
        {
            string path = Path.Combine(baseDir, @"runtime\github-update-check.txt");
            if (!File.Exists(path)) return true;
            DateTime last;
            if (!DateTime.TryParse(
                File.ReadAllText(path).Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out last))
                return true;
            return DateTime.UtcNow - last >= TimeSpan.FromHours(CheckIntervalHours);
        }

        private static void RecordCheckTime(string baseDir)
        {
            string path = Path.Combine(baseDir, @"runtime\github-update-check.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        private static string FindPackageAsset(Dictionary<string, object> release)
        {
            object rawAssets;
            if (!release.TryGetValue("assets", out rawAssets)) return null;
            IEnumerable assets = rawAssets as IEnumerable;
            if (assets == null) return null;
            foreach (object item in assets)
            {
                Dictionary<string, object> asset = item as Dictionary<string, object>;
                if (asset == null || !string.Equals(
                    GetString(asset, "name"), PackageAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string url = GetString(asset, "browser_download_url");
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("URL do pacote da release nao e HTTPS.");
                return uri.AbsoluteUri;
            }
            return null;
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static Version ParseVersion(string tag)
        {
            Match match = Regex.Match(tag ?? string.Empty, @"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:[-+].*)?$");
            if (!match.Success) throw new InvalidDataException("Tag de release invalida: " + tag);
            int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int build = match.Groups[3].Success
                ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
                : 0;
            return new Version(major, minor, build, 0);
        }

        private static bool WaitForSafeWindow(string baseDir, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            DateTime safeSince = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                bool safe;
                try { safe = IsSafeToUpdate(baseDir); }
                catch (Exception ex)
                {
                    Log(baseDir, "Nao foi possivel confirmar o estado seguro: " + ex.Message);
                    return false;
                }

                if (safe)
                {
                    if (safeSince == DateTime.MinValue) safeSince = DateTime.UtcNow;
                    if (DateTime.UtcNow - safeSince >= TimeSpan.FromSeconds(30)) return true;
                }
                else
                {
                    safeSince = DateTime.MinValue;
                }
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
            return false;
        }

        private static bool IsSafeToUpdate(string baseDir)
        {
            string statePath = Path.Combine(baseDir, @"runtime\game-mode-state.json");
            if (File.Exists(statePath))
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> state =
                    serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath));
                string mode = GetString(state, "mode");
                if (!string.Equals(mode, "normal", StringComparison.OrdinalIgnoreCase)) return false;
            }

            string configPath = Path.Combine(baseDir, "game-mode-config.json");
            if (!File.Exists(configPath)) throw new FileNotFoundException("Config de jogos ausente.", configPath);
            JavaScriptSerializer configSerializer = new JavaScriptSerializer();
            Dictionary<string, object> config =
                configSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            object rawNames;
            if (config == null || !config.TryGetValue("gameProcessNames", out rawNames))
                throw new InvalidDataException("Lista gameProcessNames ausente.");

            IEnumerable names = rawNames as IEnumerable;
            if (names == null) throw new InvalidDataException("Lista gameProcessNames invalida.");
            foreach (object rawName in names)
            {
                string name = Convert.ToString(rawName, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string processName = Path.GetFileNameWithoutExtension(name.Trim());
                Process[] processes = Process.GetProcessesByName(processName);
                for (int i = 0; i < processes.Length; i++) processes[i].Dispose();
                if (processes.Length > 0) return false;
            }
            return true;
        }

        private static void ExtractPackageSafely(string zipPath, string stagePath)
        {
            Directory.CreateDirectory(stagePath);
            string root = Path.GetFullPath(stagePath).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                        throw new InvalidDataException("Caminho invalido no pacote: " + entry.FullName);
                    string destination = Path.GetFullPath(Path.Combine(stagePath, relative));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Caminho fora do pacote: " + entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    string directory = Path.GetDirectoryName(destination);
                    Directory.CreateDirectory(directory);
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                        input.CopyTo(output);
                }
            }
        }

        private static string SafeName(string value)
        {
            string safe = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9._-]", "_");
            return string.IsNullOrEmpty(safe) ? "release" : safe;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void Log(string baseDir, string message)
        {
            try
            {
                string logPath = Path.Combine(baseDir, @"runtime\github-update.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Kroton auto-update logging failed: " + ex.Message + " | " + message);
            }
        }
    }
}
