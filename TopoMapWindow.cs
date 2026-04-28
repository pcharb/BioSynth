using System.Windows;
using System.Windows.Media;

namespace BioSynth
{
    /// <summary>
    /// Fenêtre séparée pour la carte topographique 2D EEG.
    /// Ouverte via le bouton "TOPOMAP 2D" dans le panneau EEG.
    /// Reste synchronisée avec les données en temps réel via BrainZoneController.
    /// </summary>
    public class TopoMapWindow : Window
    {
        public readonly EEGTopoMap TopoMap;

        public TopoMapWindow(BrainZoneController zones, int channelCount, int sampleRate)
        {
            Title  = "Carte Topographique EEG — Temps Réel";
            Width  = 560;
            Height = 620;
            Background          = new SolidColorBrush(Color.FromRgb(5, 8, 18));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode          = ResizeMode.CanResizeWithGrip;

            TopoMap = new EEGTopoMap();
            TopoMap.Configure(zones, channelCount, sampleRate);
            Content = TopoMap;
        }

        /// <summary>Reconfigure après un changement de nombre de canaux.</summary>
        public void Reconfigure(BrainZoneController zones, int channelCount, int sampleRate)
            => TopoMap.Configure(zones, channelCount, sampleRate);
    }
}
