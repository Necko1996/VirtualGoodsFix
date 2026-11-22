using System.Reflection;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.Simulation;
using Unity.Entities;
using VirtualGoodsFix.Systems;

namespace VirtualGoodsFix
{
    public class Mod : IMod
    {
        public static string Name => Assembly.GetExecutingAssembly().GetName().Name;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
        public static string InformationalVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        public static ILog log = LogManager.GetLogger($"{nameof(VirtualGoodsFix)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting m_Setting { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
            }

            log.Info(Name + ' ' + Version + ' ' + InformationalVersion);

            World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<ResourceBuyerSystem>().Enabled = false;

            updateSystem.UpdateAt<VGFResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
