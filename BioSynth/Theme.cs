using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BioSynth
{
    /// <summary>
    /// Thème clair / sombre de l'application.
    ///
    /// Les couleurs sont définies dans Themes/Dark.xaml et Themes/Light.xaml sous deux formes :
    /// Color.&lt;Nom&gt; (pour le code-behind via Theme.Color / Theme.Brush) et Brush.&lt;Nom&gt;
    /// (pour le XAML via {DynamicResource}). Changer de thème remplace le dictionnaire fusionné
    /// de l'application ; tout ce qui est lié par DynamicResource se met à jour immédiatement.
    ///
    /// Par défaut l'application suit le réglage Windows « Couleur par défaut des applications »
    /// et réagit en direct quand l'utilisateur le change (SystemEvents.UserPreferenceChanged).
    /// </summary>
    public static class Theme
    {
        public static bool IsDark       { get; private set; } = true;
        /// <summary>Vrai (défaut) : suivre Windows. Faux : garder le thème appliqué manuellement.</summary>
        public static bool FollowSystem { get; set; } = true;

        /// <summary>Déclenché après chaque changement de thème (sur le thread UI).</summary>
        public static event Action? ThemeChanged;

        private static bool _initialized;

        private static readonly string PrefPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BioSynth", "theme.txt");

        /// <summary>Préférence persistée : "Auto", "Light" ou "Dark".</summary>
        public static string LoadPreference()
        {
            try { var v = System.IO.File.ReadAllText(PrefPath).Trim(); return v is "Light" or "Dark" ? v : "Auto"; }
            catch { return "Auto"; }
        }

        public static void SavePreference(string pref)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefPath)!);
                System.IO.File.WriteAllText(PrefPath, pref);
            }
            catch { /* préférence non critique */ }
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            string pref = LoadPreference();
            FollowSystem = pref == "Auto";
            Apply(pref switch { "Light" => false, "Dark" => true, _ => IsSystemDark() });
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!FollowSystem) return;
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                bool dark = IsSystemDark();
                if (dark != IsDark) Apply(dark);
            });
        }

        /// <summary>Lit HKCU\...\Themes\Personalize\AppsUseLightTheme (0 = sombre). Sombre si absent.</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
            }
            catch { return true; }
        }

        public static void Apply(bool dark)
        {
            var app = Application.Current;
            if (app == null) return;
            var dict = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
            var merged = app.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
                if (merged[i].Contains("Color.BgWindow")) merged.RemoveAt(i);
            merged.Insert(0, dict);
            IsDark = dark;
            ThemeChanged?.Invoke();
        }

        /// <summary>Bascule manuelle (désactive le suivi de Windows jusqu'au prochain démarrage).</summary>
        public static void Toggle() { FollowSystem = false; Apply(!IsDark); }

        public static Color Color(string key)
            => Application.Current?.TryFindResource("Color." + key) is Color c ? c : Colors.Magenta;

        public static SolidColorBrush Brush(string key)
            => Application.Current?.TryFindResource("Brush." + key) is SolidColorBrush b ? b : new SolidColorBrush(Colors.Magenta);
    }
}
