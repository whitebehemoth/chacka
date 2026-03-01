using Microsoft.Extensions.Configuration;
using System.IO;
using System.Windows;

namespace chacka
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static string UserSettingsPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "chacka",
            "user-settings.json");

        public static IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile(UserSettingsPath, optional: true, reloadOnChange: true)
            .AddUserSecrets<App>(optional: true)
            .Build();
    }
}
