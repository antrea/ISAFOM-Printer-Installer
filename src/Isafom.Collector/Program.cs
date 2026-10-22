using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isafom.Collector;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] is "--collect-output" or "--self-test-output")
            {
                using var session = new CollectorSession();
                session.RefreshQueuesAsync().GetAwaiter().GetResult();
                var choices = args[0] == "--self-test-output"
                    ? session.AvailableQueues.Where(q => q.DriverName == "Microsoft Print To PDF").ToArray()
                    : session.AvailableQueues.Where(q => q.ModelHint).ToArray();
                if (choices.Length != 1) return 2; // use GUI for explicit choice if absent/ambiguous
                session.SelectQueue(choices[0]);
                session.CollectAsync().GetAwaiter().GetResult();
                ReportWriter.Export(args[1], session.Bundle());
                return 0;
            }
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                MessageBox.Show("Operazione non completata. Nessun dettaglio personale è stato registrato. Rivolgersi ad Andrea Esposito.", "Raccolta Kyocera", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            if (args.Length == 2 && args[0] is "--render-preview" or "--render-compact-preview")
            {
                var window = new CollectorWindow();
                int width = args[0] == "--render-compact-preview" ? 620 : 1020;
                int height = args[0] == "--render-compact-preview" ? 400 : 800;
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(Path.GetFullPath(args[1]), FileMode.CreateNew); encoder.Save(file);
                window.Close();
                return 0;
            }
            if (args.Length != 0) return 2;
            return app.Run(new CollectorWindow());
        }
        catch
        {
            if (args.Length == 0) MessageBox.Show("Impossibile avviare la raccolta. Rivolgersi ad Andrea Esposito.", "Raccolta Kyocera", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
