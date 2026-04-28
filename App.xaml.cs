using System;
using System.IO;
using System.Windows;

namespace BioSynth
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var msg = FlattenException(ex.ExceptionObject as Exception);
                File.WriteAllText("crash.log", msg);
                MessageBox.Show(msg, "Erreur critique", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, ex) =>
            {
                var msg = FlattenException(ex.Exception);
                File.WriteAllText("crash.log", msg);
                MessageBox.Show(msg, "Erreur UI", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }

        private static string FlattenException(Exception? ex)
        {
            var sb = new System.Text.StringBuilder();
            while (ex != null)
            {
                sb.AppendLine($"[{ex.GetType().FullName}]");
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.StackTrace);
                sb.AppendLine("--- Inner ---");
                ex = ex.InnerException;
            }
            return sb.ToString();
        }
    }
}
