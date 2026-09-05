using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace ABSwitchBack.Revit
{
    /// <summary>
    /// Ribbon images, loaded from resources embedded in this assembly so the deployed
    /// add-in folder stays at two DLLs and no image path can go missing.
    /// </summary>
    internal static class Icons
    {
        private const string Prefix = "ABSwitchBack.Revit.Resources.";

        public static BitmapSource LogoSmall { get { return LoadPng(Prefix + "logo_16.png"); } }
        public static BitmapSource LogoLarge { get { return LoadPng(Prefix + "logo_32.png"); } }
        public static BitmapSource StatusSmall { get { return LoadPng(Prefix + "status_16.png"); } }
        public static BitmapSource StatusLarge { get { return LoadPng(Prefix + "status_32.png"); } }
        public static BitmapSource LinkedInSmall { get { return LoadIcon(Prefix + "linkedin_16.ico"); } }
        public static BitmapSource LinkedInLarge { get { return LoadIcon(Prefix + "linkedin_32.ico"); } }

        /// <summary>WPF decodes PNG natively, so the logo needs no .ico conversion.</summary>
        private static BitmapSource LoadPng(string resourceName)
        {
            return Load(resourceName, false);
        }

        private static BitmapSource LoadIcon(string resourceName)
        {
            return Load(resourceName, true);
        }

        private static BitmapSource Load(string resourceName, bool isIcon)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    BitmapDecoder decoder = isIcon
                        ? (BitmapDecoder)new IconBitmapDecoder(stream,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                        : new PngBitmapDecoder(stream,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count == 0) return null;

                    BitmapSource frame = decoder.Frames[0];
                    frame.Freeze();     // the ribbon may touch it from another thread
                    return frame;
                }
            }
            catch (Exception)
            {
                // A missing icon is cosmetic; it must never stop the ribbon from building.
                return null;
            }
        }
    }
}
