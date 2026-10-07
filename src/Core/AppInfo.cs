using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

[assembly: AssemblyProduct("Battery Check")]
[assembly: AssemblyTitle("Battery Check")]
[assembly: AssemblyVersion(BatteryCheck.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(BatteryCheck.AppInfo.Version + ".0")]

namespace BatteryCheck
{
    /// <summary>Версия программы и откуда брать обновления. Версию поднимает tools\release.ps1 перед выпуском.</summary>
    static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Repo = "nik-agilefox/BatteryCheck";

        /// <summary>Имя архива с программой в релизе: его скачивает обновление.</summary>
        public static string PackageName(string version) { return "BatteryCheck-" + version + ".zip"; }
    }

    /// <summary>Последний релиз на GitHub — то, что нужно для обновления.</summary>
    sealed class ReleaseInfo
    {
        public string Version;      // «1.2.0» (из тега «v1.2.0»)
        public string PageUrl;      // страница релиза — для подсказки «что нового»
        public string Notes;        // текст релиза
        public string PackageUrl;   // архив BatteryCheck-<версия>.zip
        public long PackageSize;
        public string PackageSha256;  // из поля digest («sha256:…»), если GitHub его отдал; иначе null

        /// <summary>Разбор ответа api.github.com/repos/…/releases/latest; null — не релиз или в нём нет архива программы.</summary>
        public static ReleaseInfo Parse(string json)
        {
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch (Exception) { return null; }
            if (root == null) return null;
            string tag = Str(root, "tag_name");
            if (string.IsNullOrEmpty(tag) || Bool(root, "draft") || Bool(root, "prerelease")) return null;
            var r = new ReleaseInfo
            {
                Version = tag.TrimStart('v', 'V'),
                PageUrl = Str(root, "html_url"),
                Notes = Str(root, "body") ?? "",
            };
            object assets;
            if (!root.TryGetValue("assets", out assets) || !(assets is IEnumerable)) return null;
            foreach (var a in (IEnumerable)assets)
            {
                var d = a as Dictionary<string, object>;
                if (d == null || !string.Equals(Str(d, "name"), AppInfo.PackageName(r.Version), StringComparison.OrdinalIgnoreCase)) continue;
                r.PackageUrl = Str(d, "browser_download_url");
                object size;
                if (d.TryGetValue("size", out size) && size != null) r.PackageSize = Convert.ToInt64(size);
                string digest = Str(d, "digest");
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.PackageSha256 = digest.Substring(7).ToLowerInvariant();
            }
            return r.PackageUrl != null && r.PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? r : null;
        }

        /// <summary>Версия a новее b (1.10.0 новее 1.9.3); неразборчивая версия новее не считается.</summary>
        public static bool IsNewer(string a, string b)
        {
            Version va, vb;
            return System.Version.TryParse(a, out va) && System.Version.TryParse(b, out vb) && va > vb;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }
    }
}
