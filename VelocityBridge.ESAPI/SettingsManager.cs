using System;
using System.IO;
using Newtonsoft.Json;

namespace AutoFLC
{
    /// <summary>
    /// Persistable user settings.
    /// </summary>
    public class UserSettings
    {
        public string LastOutputDirectory { get; set; }
        public string LastBdfPath { get; set; }
        public bool UseStep1Reference { get; set; }
        public string ReferenceDirectory { get; set; }

        public UserSettings()
        {
            UseStep1Reference = true;
            AutoImportRtstruct = true;
            PlastiUseRoi = true;
            PlastiLambda = 1.0;
            PlastiPresetIndex = 0;
        }

        public string DaemonAETitle { get; set; }
        public string DaemonIP { get; set; }
        public int DaemonPort { get; set; }
        public string LocalAETitle { get; set; }
        public int LocalPort { get; set; }
        public bool AutoImportRtstruct { get; set; }



        // Plastimatch engine (Step 1 third branch)
        /// <summary>Path to plastimatch.exe; empty = auto-probe the default installs.</summary>
        public string PlastimatchExePath { get; set; }
        /// <summary>Stage preset index (0 = Lim 2026 six-stage baseline).</summary>
        public int PlastiPresetIndex { get; set; }
        /// <summary>B-spline regularization lambda (validated default 1.0).</summary>
        public double PlastiLambda { get; set; }
        /// <summary>Limit the registration to the lung fixed_roi (validated default).</summary>
        public bool PlastiUseRoi { get; set; }
    }

    /// <summary>
    /// JSON settings manager that stores settings under
    /// %LOCALAPPDATA%\AutoFLC.
    /// </summary>
    public static class SettingsManager
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoFLC");

        private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonConvert.DeserializeObject<UserSettings>(json) ?? new UserSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to load settings: " + ex.Message);
            }
            return new UserSettings();
        }

        public static void Save(UserSettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save settings: " + ex.Message);
            }
        }
    }
}
