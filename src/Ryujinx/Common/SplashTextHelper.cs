using System.Collections.Generic;
using Ryujinx.Common.Logging;
using Gommon;
using Ryujinx.Ava.Systems.Configuration;
using System.Text.Json;

namespace Ryujinx.Common
{
    public class SplashTextHelper
    {
        public static void PrintSplash()
        {
            string splash = GetSplash();
            
            Logger.Notice.Print(LogClass.Application,  "   ___                 __    _              ");
            Logger.Notice.Print(LogClass.Application, @"  / _ \  __ __ __ __  / /   (_)  ___   ___ _");
            Logger.Notice.Print(LogClass.Application, @" / , _/ / // // // / / _ \ / /  / _ \ / _ `/");
            Logger.Notice.Print(LogClass.Application, @"/_/|_|  \_, / \_,_/ /_.__//_/  /_//_/ \_, / ");
            Logger.Notice.Print(LogClass.Application,  "       /___/                         /___/  ");
            
            if (splash is null)
            {
                Logger.Error?.Print(LogClass.Application, "Failed to fetch Splash Text! Splash JSON is invalid!");
                return;
            }
            
            if (!splash.IsNullOrEmpty())
            {
                Logger.Notice.Print(LogClass.Application, "");
                Logger.Notice.Print(LogClass.Application, splash);
                Logger.Notice.Print(LogClass.Application, "");
            }
        }

        private static string _finalSplash;

        public static string GetSplash()
        {
            if (_finalSplash is null)
            {
                try
                {
                    string data;
                    data = EmbeddedResources.ReadAllText("Ryujinx/Assets/Splashes.json");
                    SplashLocales splashJson = JsonSerializer.Deserialize<SplashLocales>(data);
                    _finalSplash = splashJson.Locales[ConfigurationState.Instance.UI.LanguageCode.Value].GetRandomElement() ?? "";
                }
                catch
                {
                    return null;
                }
            }
            
            return _finalSplash;
        }

        private struct SplashLocales
        {
            public Dictionary<string, List<string>> Locales { get; set; }
        }

    }

}
