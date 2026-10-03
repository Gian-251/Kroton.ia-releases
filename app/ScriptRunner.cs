using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Kroton
{
    /// <summary>Resultado de execucao de um comando externo.</summary>
    public struct PsResult
    {
        public int ExitCode;
        public string Output;

        public PsResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output ?? string.Empty;
        }
    }

    /// <summary>
    /// Executa os scripts PowerShell do Kroton.ia capturando saida e erros.
    /// Todos os caminhos sao resolvidos relativos ao proprio .exe,
    /// entao funciona tanto em d:\Kroton.ia quanto em C:\Program Files\KrotonIA.
    /// </summary>
    internal static class ScriptRunner
    {
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static string PowerShellExe
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
            }
        }

        /// <summary>
        /// Executa um comando PowerShell (-Command) com saida UTF-8 e timeout.
        /// </summary>
        public static string RunPs(string command, int timeoutMs, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = PowerShellExe;
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                "\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = BaseDir;

            return Run(psi, timeoutMs, out exitCode);
        }

        /// <summary>Executa um exe do sistema (ex.: schtasks.exe).</summary>
        public static string RunExe(string fileName, string arguments, int timeoutMs, out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = BaseDir;

            return Run(psi, timeoutMs, out exitCode);
        }

        private static string Run(ProcessStartInfo psi, int timeoutMs, out int exitCode)
        {
            StringBuilder sb = new StringBuilder();

            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } }
                };

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); }
                    catch { }
                    exitCode = -1;
                    lock (sb) { sb.AppendLine("[TIMEOUT]"); return sb.ToString(); }
                }

                // Aguarda de novo sem timeout para drenar a saida assincrona
                p.WaitForExit();
                exitCode = p.ExitCode;
                lock (sb) { return sb.ToString(); }
            }
        }

        /// <summary>Extrai o primeiro bloco JSON de uma saida de comando.</summary>
        public static string ExtractJson(string output)
        {
            if (string.IsNullOrEmpty(output)) return string.Empty;
            int a = output.IndexOf('{');
            int b = output.LastIndexOf('}');
            if (a < 0 || b <= a) return string.Empty;
            return output.Substring(a, b - a + 1);
        }

        /// <summary>Retorna as ultimas N linhas de um texto.</summary>
        public static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string[] all = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int start = Math.Max(0, all.Length - lines);
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < all.Length; i++)
            {
                sb.AppendLine(all[i].TrimEnd('\r'));
            }
            return sb.ToString();
        }
    }
}
