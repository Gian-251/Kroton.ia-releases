using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Kroton
{
    internal static class ServiceDescriptionFetcher
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static System.Collections.Generic.Dictionary<string, string> Cache = null;

        private static string CachePath
        {
            get { return Path.Combine(ScriptRunner.BaseDir, @"runtime\services-desc-cache.json"); }
        }

        private static void EnsureCacheLoaded()
        {
            if (Cache != null) return;
            Cache = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Cache["WaaSMedicSvc"] = "Serviço de Reparo e Diagnóstico do Windows Update.";
            Cache["LSM"] = "Gerenciador de Sessão Local (kernel do Windows - sessões de usuário).";
            Cache["NetSetupSvc"] = "Serviço de Instalação e Configuração de Redes do Windows.";
            Cache["AzureAttestService"] = "Serviço de Atestação do Microsoft Azure.";
            Cache["mysql"] = "Servidor de Banco de Dados MySQL / MariaDB.";
            Cache["ProtonVPN Service"] = "Serviço em segundo plano da VPN Proton.";

            try
            {
                if (File.Exists(CachePath))
                {
                    string json = File.ReadAllText(CachePath);
                    var dict = Serializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict) Cache[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private static void SaveCache()
        {
            try
            {
                if (Cache == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, Serializer.Serialize(Cache), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        public static string FetchDescription(string serviceName, string displayName)
        {
            EnsureCacheLoaded();

            string cached;
            if (Cache.TryGetValue(serviceName, out cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                string query = !string.IsNullOrEmpty(serviceName) ? serviceName : displayName;
                string msDesc = SearchMicrosoftLearn(query);
                if (!string.IsNullOrEmpty(msDesc))
                {
                    Cache[serviceName] = msDesc;
                    SaveCache();
                    return msDesc;
                }

                string wikiDesc = SearchWikipedia(displayName);
                if (!string.IsNullOrEmpty(wikiDesc))
                {
                    Cache[serviceName] = wikiDesc;
                    SaveCache();
                    return wikiDesc;
                }
            }
            catch { }

            return "Descrição não disponível na documentação oficial.";
        }

        private static string SearchMicrosoftLearn(string term)
        {
            try
            {
                string url = "https://learn.microsoft.com/api/search?search=" +
                    Uri.EscapeDataString(term + " service Windows") + "&locale=pt-br&$top=1";

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.UserAgent = "KrotonIA/1.2";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    var obj = Serializer.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                    if (obj != null && obj.ContainsKey("results"))
                    {
                        var results = obj["results"] as Array;
                        if (results != null && results.Length > 0)
                        {
                            var top = results.GetValue(0) as System.Collections.Generic.Dictionary<string, object>;
                            if (top != null && top.ContainsKey("description") && top["description"] != null)
                            {
                                string d = CleanText(top["description"].ToString());
                                if (d.Length > 160) d = d.Substring(0, 157) + "...";
                                return d;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string SearchWikipedia(string term)
        {
            try
            {
                string cleanTerm = Regex.Replace(term, "(?i)\\b(Service|Serviço|Servico|Server|Host)\\b", "").Trim();
                if (string.IsNullOrEmpty(cleanTerm)) cleanTerm = term;

                string url = "https://pt.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(cleanTerm);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.UserAgent = "KrotonIA/1.2";

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    var obj = Serializer.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                    if (obj != null && obj.ContainsKey("extract") && obj["extract"] != null)
                    {
                        string d = CleanText(obj["extract"].ToString());
                        if (d.Length > 160) d = d.Substring(0, 157) + "...";
                        return d;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string CleanText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string unescaped = WebUtility.HtmlDecode(raw);
            string noTags = Regex.Replace(unescaped, "<[^>]+>", "");
            string normalized = Regex.Replace(noTags, "\\s+", " ").Trim();
            return normalized;
        }
    }
}
