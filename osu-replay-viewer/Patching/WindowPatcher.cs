using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Platform;
using System;
using System.Reflection;

namespace osu_replay_renderer_netcore.Patching
{
    public class WindowPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Window";

        public override void DoPatching()
        {
            base.DoPatching();

            var windows = new[]
            {
                "osu.Framework.Platform.SDL2.SDL2Window",
                "osu.Framework.Platform.SDL3.SDL3Window"
            };
            
            foreach (var window in windows)
            {
                var windowType = typeof(IWindow).Assembly.GetType(window);
                if (windowType == null)
                {
                    Console.Error.WriteLine($"[WindowPatcher] Skipping missing type: {window}");
                    continue;
                }

                Patch(windowType, "get_Focused", postfix: (Delegate)SimpleReturnTrue);
                Patch(windowType, "get_Visible", postfix: (Delegate)SimpleReturnTrue);
                Patch(windowType, "set_Visible", prefix: (Delegate)CallOnlyWithFalse);
                Patch(windowType, "get_IsActive", postfix: (Delegate)SimpleReturnBindableTrue);
                Patch(windowType, "Raise", postfix: (Delegate)CallHide);
                Patch(windowType, "Show", prefix: (Delegate)OverrideToHide);
            }
        }

        private void Patch(Type type, string methodName, Delegate prefix = null, Delegate postfix = null)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null)
            {
                Console.Error.WriteLine($"[WindowPatcher] Skipping missing method: {type.FullName}.{methodName}");
                return;
            }

            Harmony.Patch(
                method,
                prefix == null ? null : new HarmonyMethod(prefix.Method),
                postfix == null ? null : new HarmonyMethod(postfix.Method));
        }

        static bool OverrideToHide(IWindow __instance)
        {
            CallHide(__instance);
            return false;
        }
        
        static void CallHide(IWindow __instance)
        {
            __instance.Hide();
        }

        static void CallOnlyWithFalse(ref bool __0)
        {
            __0 = false;
        }

        static void SimpleReturnTrue(ref bool __result)
        {
            __result = true;
        }
        
        static void SimpleReturnBindableTrue(ref IBindable<bool> __result)
        {
            __result = new Bindable<bool>(true);
        }
    }
}
