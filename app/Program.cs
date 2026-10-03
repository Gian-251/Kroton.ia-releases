using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]

namespace Kroton
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Modo de teste automatizado: nao abre GUI, nao eleva, escreve log e sai
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                return SelfTest();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // GUI de teste/desenvolvimento: sem elevacao automatica
            bool noElevate = args != null && Array.Exists(args, delegate(string a)
            {
                return a == "--no-elevate";
            });

            if (!noElevate && !ScriptRunner.IsAdmin())
            {
                return RestartElevated(args);
            }

            Application.Run(new MainForm());
            return 0;
        }

        /// <summary>Relanca o proprio exe solicitando elevacao UAC.</summary>
        private static int RestartElevated(string[] args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                if (args != null && args.Length > 0)
                {
                    psi.Arguments = string.Join(" ", args);
                }
                System.Diagnostics.Process.Start(psi);
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(
                    "O Kroton.ia precisa de permissao de administrador para funcionar.",
                    "Kroton.ia",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }
        }

        /// <summary>
        /// Teste automatizado sem GUI:
        ///  - verifica arquivos obrigatorios
        ///  - valida o JSON de configuracao
        ///  - executa a auditoria real (somente leitura)
        /// Retorna 0 = aprovado, 1 = falhou. Detalhes em %TEMP%\kroton-selftest.log
        /// </summary>
        private static int SelfTest()
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;

            log.AppendLine("Kroton.ia selftest - " + DateTime.Now.ToString("s"));
            log.AppendLine("BaseDir: " + ScriptRunner.BaseDir);
            log.AppendLine("[INFO] Admin: " + ScriptRunner.IsAdmin());

            // 1) Arquivos obrigatorios
            string[] required = {
                "Optimize-BackgroundServices.ps1",
                "KrotonGameModeRunner.ps1",
                "game-mode-config.json",
                "service-profile.json",
                "service-profile.game.json",
                "service-profile.normal.json",
                "service-profile.light.json"
            };
            foreach (string f in required)
            {
                Check(log, ref failures, File.Exists(Path.Combine(ScriptRunner.BaseDir, f)), "Arquivo: " + f);
            }

            // 2) Config JSON valido e com jogos
            try
            {
                Dictionary<string, object> cfg = LoadConfig();
                object names;
                bool ok = cfg.TryGetValue("gameProcessNames", out names)
                    && names is Array
                    && ((Array)names).Length > 0;
                Check(log, ref failures, ok, "game-mode-config.json valido com jogos");
            }
            catch (Exception ex)
            {
                Check(log, ref failures, false, "game-mode-config.json parse: " + ex.Message);
            }

            // 3) Auditoria real (somente leitura)
            try
            {
                int exit;
                string cmd = string.Format(
                    "& '{0}' -Mode Audit -ProfilePath '{1}'",
                    Path.Combine(ScriptRunner.BaseDir, "Optimize-BackgroundServices.ps1"),
                    Path.Combine(ScriptRunner.BaseDir, "service-profile.json"));
                string output = ScriptRunner.RunPs(cmd, 90000, out exit);

                bool parsed = false;
                string json = ScriptRunner.ExtractJson(output);
                if (exit == 0 && json.Length > 0)
                {
                    object obj = new JavaScriptSerializer().DeserializeObject(json);
                    parsed = obj is Dictionary<string, object>;
                }
                Check(log, ref failures, parsed, "Auditoria executa e retorna JSON");
                if (!parsed)
                {
                    log.AppendLine("--- saida bruta ---");
                    log.AppendLine(ScriptRunner.Tail(output, 10));
                }
            }
            catch (Exception ex)
            {
                Check(log, ref failures, false, "Auditoria: " + ex.Message);
            }

            string logPath = Path.Combine(Path.GetTempPath(), "kroton-selftest.log");
            try
            {
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            }
            catch { }

            log.AppendLine(failures == 0 ? "RESULTADO: APROVADO" : "RESULTADO: FALHOU (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Le e desserializa game-mode-config.json.</summary>
        internal static Dictionary<string, object> LoadConfig()
        {
            string path = Path.Combine(ScriptRunner.BaseDir, "game-mode-config.json");
            string text = File.ReadAllText(path);
            object obj = new JavaScriptSerializer().DeserializeObject(text);
            Dictionary<string, object> dict = obj as Dictionary<string, object>;
            if (dict == null) throw new InvalidDataException("JSON raiz nao e um objeto");
            return dict;
        }

        private static void Check(StringBuilder sb, ref int failures, bool ok, string name)
        {
            sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name);
            if (!ok) failures++;
        }
    }
}
