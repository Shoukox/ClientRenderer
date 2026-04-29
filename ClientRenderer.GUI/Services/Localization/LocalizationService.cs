using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace ClientRenderer.GUI.Services.Localization
{
    public sealed class LocalizationService : ObservableObject
    {
        private static readonly ResourceManager ResourceManager = new(
            typeof(LocalizationService).Namespace + ".Strings",
            Assembly.GetExecutingAssembly());

        private CultureInfo _currentCulture = CultureInfo.GetCultureInfo("en");

        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } =
        [
            new("en", "English"),
            new("de", "Deutsch"),
            new("ru", "Русский")
        ];

        public CultureInfo CurrentCulture
        {
            get => _currentCulture;
            private set => SetProperty(ref _currentCulture, value);
        }

        public string this[string key] => ResourceManager.GetString(key, CurrentCulture) ?? key;

        public void SetLanguage(string? languageCode)
        {
            var normalizedCode = NormalizeLanguageCode(languageCode);
            var culture = CultureInfo.GetCultureInfo(normalizedCode);

            if (Equals(CurrentCulture, culture))
                return;

            CurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            OnPropertyChanged("Item");
            OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? LanguageChanged;

        private string NormalizeLanguageCode(string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return "en";

            foreach (var language in SupportedLanguages)
            {
                if (string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase))
                    return language.Code;
            }

            return "en";
        }
    }
}
