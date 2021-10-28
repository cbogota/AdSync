using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;

namespace AdSync
{
    [RunInstaller(true)]
    public partial class Installer : System.Configuration.Install.Installer
    {
        public Installer()
        {
            //InitializeComponent();
            //((PerformanceCategory)
            // Health.Health.GetHealthIndicator("AdSync", "ForInstallation"))
            //    .Install();
        }
    }
}
