using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BatteryCheck
{
    /// <summary>
    /// Обновление из релизов GitHub (публичный репозиторий, без ключей). Проверка — последний релиз через API; обновление —
    /// архив BatteryCheck-&lt;версия&gt;.zip: скачать во временную папку, сверить размер и SHA-256 (если GitHub его отдал),
    /// распаковать рядом, затем подменить файлы в папке программы. Запущенный exe Windows не даёт перезаписать, но даёт
    /// переименовать — старый становится *.old и удаляется при следующем запуске. Логи и настройки не трогаются.
    /// Все методы, кроме CleanupOld, блокирующие — вызывать не из потока окна.
    /// </summary>
    static class Updater
    {
        static string ApiUrl { get { return "https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest"; } }

        static string BinDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>Запущено из папки с исходниками (сборка разработчика): обновлять её релизом нельзя — затрёт свежую сборку.</summary>
        public static bool IsDevBuild
        {
            get { return Directory.Exists(Path.Combine(BinDir, "..", "src")) && File.Exists(Path.Combine(BinDir, "..", "build.cmd")); }
        }

        /// <summary>Последний релиз; null — релизов нет или в нём нет архива программы (error = null), либо ошибка сети (error).</summary>
        public static ReleaseInfo Check(out string error)
        {
            error = null;
            try
            {
                using (var resp = (HttpWebResponse)Request(ApiUrl, "application/vnd.github+json").GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return ReleaseInfo.Parse(sr.ReadToEnd());
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.NotFound) return null;  // релизов ещё нет
                error = e.Message;
            }
            catch (Exception e) { error = e.Message; }
            return null;
        }

        /// <summary>Скачать, проверить и подменить файлы. progress — проценты загрузки (из рабочего потока). true — можно перезапускать.</summary>
        public static bool Install(ReleaseInfo r, Action<int> progress, out string error)
        {
            error = null;
            string work = Path.Combine(Path.GetTempPath(), "BatteryCheck-update-" + r.Version);
            string zip = Path.Combine(work, AppInfo.PackageName(r.Version));
            string unpacked = Path.Combine(work, "files");
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(unpacked);
                Download(r.PackageUrl, zip, r.PackageSize, progress);

                long size = new FileInfo(zip).Length;
                if (r.PackageSize > 0 && size != r.PackageSize) { error = string.Format("size {0} instead of {1}", size, r.PackageSize); return false; }
                if (r.PackageSha256 != null && Sha256(zip) != r.PackageSha256) { error = "SHA-256 mismatch"; return false; }

                var files = Unpack(zip, unpacked);
                if (!files.Contains("BatteryCheckGui.exe")) { error = "BatteryCheckGui.exe not in the package"; return false; }
                Replace(unpacked, files);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
        }

        /// <summary>Запустить новую версию: она дождётся выхода этой (--after) и откроет окно.</summary>
        public static void StartNewVersion()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(BinDir, "BatteryCheckGui.exe"), "--after " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = false,
                WorkingDirectory = BinDir,
            });
        }

        /// <summary>Удалить *.old, оставшиеся от прошлого обновления (при запуске: старый процесс уже вышел).</summary>
        public static void CleanupOld()
        {
            try
            {
                foreach (var f in Directory.GetFiles(BinDir, "*.old"))
                    try { File.Delete(f); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        static HttpWebRequest Request(string url, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "BatteryCheck/" + AppInfo.Version;  // GitHub отклоняет запросы без User-Agent
            req.Accept = accept;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        static void Download(string url, string path, long expected, Action<int> progress)
        {
            using (var resp = Request(url, "application/octet-stream").GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
            {
                long total = expected > 0 ? expected : resp.ContentLength, done = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    int pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : -1;
                    if (pct != last && progress != null) progress(pct);
                    last = pct;
                }
            }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Только файлы в корне архива с понятными расширениями: путь вида «..\x» не выйдет за папку распаковки.</summary>
        static HashSet<string> Unpack(string zip, string dir)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var a = ZipFile.OpenRead(zip))
                foreach (var e in a.Entries)
                {
                    string name = e.FullName;
                    if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.StartsWith(".")) continue;
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext != ".exe" && ext != ".dll" && ext != ".json" && ext != ".txt" && ext != ".config") continue;
                    e.ExtractToFile(Path.Combine(dir, name), true);
                    names.Add(name);
                }
            return names;
        }

        /// <summary>Подмена с откатом: если какой-то файл не встал, вернуть все прежние.</summary>
        static void Replace(string from, HashSet<string> files)
        {
            var moved = new List<string>();
            var placed = new List<string>();
            try
            {
                foreach (var name in files)
                {
                    string target = Path.Combine(BinDir, name), old = target + ".old";
                    if (File.Exists(target))
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(target, old);  // работает и для запущенного exe
                        moved.Add(name);
                    }
                    File.Copy(Path.Combine(from, name), target);
                    placed.Add(name);
                }
            }
            catch (Exception)
            {
                foreach (var name in placed) try { File.Delete(Path.Combine(BinDir, name)); } catch (Exception) { }
                foreach (var name in moved) try { File.Move(Path.Combine(BinDir, name) + ".old", Path.Combine(BinDir, name)); } catch (Exception) { }
                throw;
            }
        }
    }
}
