using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AuroraLauncher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _ = Task.Run(ModNamesService.Warm);
            try
            {
                var settings = BridgeService.GetSettings();
                if (settings["autoImportVersions"]?.GetValue<bool>() != false)
                {
                    InstanceArchiveService.ImportExisting(true);
                }
            }
            catch { }
        }
    }
}
