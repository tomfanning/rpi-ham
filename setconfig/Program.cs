using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace setconfig
{
    class Program
    {
        const string dwTemplate = "/root/direwolf.template.conf";
        const string dwConfDestination = "/tmp/direwolf.conf";

        const string wpaTemplate = "/root/wpa_supplicant.template.conf";
        const string wpaDest = "/etc/wpa_supplicant/wpa_supplicant.conf";

        const string icesTemplate = "/root/ices-conf.template.xml";
        const string icesDest = "/tmp/ices-conf.xml";

        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Expected one argument - path to JSON config file");
                return -1;
            }

            string fn = args[0];
            if (!File.Exists(fn))
            {
                Console.WriteLine("File not found: " + fn);
                return -1;
            }

            if (!File.Exists(dwTemplate))
            {
                Console.WriteLine("File not found: " + dwTemplate);
                return -1;
            }

            if (!File.Exists(wpaTemplate))
            {
                Console.WriteLine("File not found: " + wpaTemplate);
                return -1;
            }

            Config cfg;
            try
            {
                cfg = SimpleJson.SimpleJson.DeserializeObject<Config>(File.ReadAllText(fn));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error reading config: " + ex.GetBaseException().Message);
                return -1;
            }

            if (cfg.Mycall == "N0CALL")
            {
                Console.WriteLine($"Error: Callsign not configured in {fn}, disabling Direwolf.");
                if (File.Exists(dwConfDestination))
                {
                    File.Delete(dwConfDestination);
                }
            }
            else
            {
                string newDwConf = File.ReadAllText(dwTemplate)
                    .Replace("$mycall", cfg.Mycall)
                    .Replace("$igserver", cfg.Igserver)
                    .Replace("$passcode", DoAprsHash(cfg.Mycall).ToString())
                    .Replace("$lat", FormatLatLon(cfg.Lat, true))
                    .Replace("$lon", FormatLatLon(cfg.Lon, false))
                    .Replace("$modem", cfg.Modem);

                File.WriteAllText(dwConfDestination, newDwConf);
                Console.WriteLine("Wrote direwolf config to " + dwConfDestination);
            }

            File.WriteAllText("/etc/aprs-ppm", cfg.RTLPPM.ToString());

            string wpaHashBefore;
            if (File.Exists(wpaDest))
            {
                wpaHashBefore = BitConverter.ToString(MD5.Create().ComputeHash(File.ReadAllBytes(wpaDest)));
            }
            else
            {
                wpaHashBefore = null;
            }

            if (cfg.Wifis.Length > 0)
            {
                string newWpaConfig = BuildWpaConfig(cfg.Wifis);
                if (!File.Exists(wpaDest) || File.ReadAllText(wpaDest) != newWpaConfig)
                {
                    File.WriteAllText(wpaDest, newWpaConfig);
                    Console.WriteLine("Wrote Wi-Fi config to " + wpaDest);
                }
            }

            string newIcesConf = File.ReadAllText(icesTemplate).Replace("$mycall", cfg.Mycall);
            if (!File.Exists(icesDest) || File.ReadAllText(icesDest) != newIcesConf)
            {
                File.WriteAllText(icesDest, newIcesConf);
                Console.WriteLine("Wrote ices2 config to " + icesDest);
            }

            if (!String.IsNullOrWhiteSpace(cfg.RootPassword))
            {
                ExecuteProcess("/bin/bash", $"-c 'echo root:{cfg.RootPassword.Trim()} | chpasswd'");
                Console.WriteLine("Set root password");
            }

            ConfigureSlack(cfg);

            if (wpaHashBefore == null || BitConverter.ToString(MD5.Create().ComputeHash(File.ReadAllBytes(wpaDest))) != wpaHashBefore)
            {
                Console.WriteLine("Rebooting to pick up Wi-Fi settings...");
                ExecuteProcess("/sbin/reboot");
            }
            else
            {
                Console.WriteLine("Wi-Fi not changed, not rebooting");
            }

            return 0;
        }

        static void ConfigureSlack(Config cfg)
        {
            const string slackConf = "/tmp/slack.conf";
            if (string.IsNullOrWhiteSpace(cfg.SlackWebhookUrl))
            {
                if (File.Exists(slackConf))
                {
                    File.Delete(slackConf);
                    Console.WriteLine("Removed Slack webhook");
                }
            }
            else
            {
                if (!File.Exists(slackConf) || File.ReadAllText(slackConf) != cfg.SlackWebhookUrl.Trim())
                {
                    File.WriteAllText(slackConf, cfg.SlackWebhookUrl.Trim());
                    Console.WriteLine("Configured Slack webhook");
                }
            }
        }

        static string BuildWpaConfig(Network[] wifis)
        {
            var sb = new StringBuilder(File.ReadAllText(wpaTemplate));

            foreach (Network n in wifis)
            {
                sb.AppendLine();
                string networkBlock = GetNetworkBlock(n.SSID, n.Key).Trim();

                // cut off last curly brace
                networkBlock = networkBlock.Substring(0, networkBlock.Length - 1).Trim();

                sb.AppendLine(networkBlock);

                // allow connection to hidden network
                sb.AppendLine("        scan_ssid=1");

                // put curly brace back
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        static string GetNetworkBlock(string sSID, string key)
        {
            return ExecuteProcess("/usr/bin/wpa_passphrase", sSID + " " + key).Stdout;
        }

        static ProcessResult ExecuteProcess(string process, string args = null, string stdin = null)
        {
            Process p = new Process();
            p.StartInfo = new ProcessStartInfo(process, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            if (!string.IsNullOrWhiteSpace(stdin))
            {
                p.StartInfo.RedirectStandardInput = true;
            }

            p.Start();

            if (!string.IsNullOrWhiteSpace(stdin))
            {
                p.StandardInput.WriteLine(stdin);
                p.StandardInput.Write((char)4);
            }

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return new ProcessResult { Stdout = output, ExitCode = p.ExitCode };
        }

        class ProcessResult
        {
            public int ExitCode { get; set; }
            public string Stdout { get; set; }
        }

        static string FormatLatLon(double o, bool isLat)
        {
            // e.g. 51^26.84N

            string gtLetter, ltLetter;
            if (isLat)
            {
                gtLetter = "N";
                ltLetter = "S";
            }
            else
            {
                gtLetter = "E";
                ltLetter = "W";
            }

            double a = Math.Abs(o);

            string deg = Math.Floor(a).ToString("0");
            double decMin = (a - Math.Floor(a)) * 60.0;
            string letter = o > 0 ? gtLetter : ltLetter;

            string ret = $"{deg}^{decMin:0.00}{letter}";

            return ret;
        }

        static int DoAprsHash(string call)
        {
            string upper = call.ToUpper();
            string main = upper.Split('-')[0];

            int hash = 0x73e2;

            char[] chars = main.ToCharArray();

            while (chars.Length != 0)
            {
                char? one = shift(ref chars);
                char? two = shift(ref chars);
                hash = hash ^ one.Value << 8;

                if (two != null)
                {
                    hash = hash ^ two.Value;
                }
            }

            int result = hash & 0x7fff;

            return result;
        }

        static char? shift(ref char[] chars)
        {
            if (chars.Length == 0)
                return null;

            char result = chars[0];

            char[] newarr = new char[chars.Length - 1];

            for (int i = 1; i < chars.Length; i++)
            {
                newarr[i - 1] = chars[i];
            }

            chars = newarr;

            return result;
        }
    }

    class Config
    {
        //string configTemplate = SimpleJson.SimpleJson.SerializeObject(new Config { Lat=123.45, Lon = 234.56, Modem = "1200", Mycall = "call123", Wifis = new[] { new Network { SSID = "mynet", Key = "somepass" } } });

        public Config()
        {
            Igserver = "euro.aprs2.net";
            Modem = "1200";
        }

        public string Mycall { get; set; }
        public string Igserver { get; set; }
        public Network[] Wifis { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Modem { get; set; }
        public string RootPassword { get; set; }
        public int? RTLPPM { get; set; }
        public string SlackWebhookUrl { get; set; }
    }

    class Network
    {
        public string SSID { get; set; }
        public string Key { get; set; }
    }
}
