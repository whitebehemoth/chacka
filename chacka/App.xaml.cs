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
        public static string AppSettingsPath { get; } = Path.Combine(
            AppContext.BaseDirectory,
            "appsettings.json");

        public static IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(AppSettingsPath, optional: true, reloadOnChange: true)
            .AddUserSecrets<App>(optional: true)
            .Build();
    }
}
